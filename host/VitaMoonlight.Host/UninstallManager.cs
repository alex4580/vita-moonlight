namespace VitaMoonlight.Host;

internal sealed record SunshineIntegrationCleanupResult(
    string ConfigurationDirectory,
    int RemovedHooks,
    bool RemovedGeneratedApplication,
    bool RemovedNativeDisplaySettings);

internal sealed record UninstallPreparationResult(
    IReadOnlyList<string> PhysicalDisplays,
    bool ClearedSavedTransaction);

internal sealed record OwnedStateCleanupResult(
    int RemovedFiles,
    int RemovedDirectories,
    IReadOnlyList<string> RetainedEntries);

internal sealed record UninstallFinalizationResult(
    IReadOnlyList<string> PhysicalDisplays,
    bool RescueAgentRemoved,
    bool RecoveryTaskRemoved,
    OwnedStateCleanupResult StateCleanup,
    IReadOnlyList<string> Warnings);

internal static class UninstallManager
{
    private const int PhysicalRecoveryAttemptsAfterRescan = 5;
    private const int PhysicalRecoveryAttemptsAfterVddRestart = 15;
    private const int PhysicalRecoveryRetryDelayMilliseconds = 250;
    private static readonly string[] CurrentRootStateFiles =
    [
        "display-recovery.json",
        "audio-recovery.json",
        "audio-recovery.backup.json",
        "audio-recovery.lock",
        "host-settings.json",
        "session.lock",
        "last-command-error.txt",
        "display-driver-verification.json",
        "display-driver-directory-identity.json",
        "backend-lifecycle.json",
        "backend-lifecycle.backup.json",
        "backend-lifecycle.lock",
        "backend-disabled.intent",
        "deferred-host-setup.json",
        "display-suspend.intent",
        "display-suspend.lock",
        "installer-maintenance.json",
        "installer-maintenance.backup.json",
        "installer-maintenance.lock",
        "stream-boundary-lease.json",
        "stream-boundary-lease.backup.json",
        "stream-boundary-lease.lock",
    ];

    private static readonly string[] DiagnosticStateFiles =
    [
        "stream-rescue-status.json",
        "stream-rescue.log",
    ];

    internal static UninstallPreparationResult Prepare(
        bool beginUninstallTransaction = false)
    {
        var operationLock = BackendLifecycleStateStore.AcquireLock();
        try
        {
            if (beginUninstallTransaction)
            {
                RequireOwnedFinalizationPreflight();
                // The actual uninstaller holds this durable intent across the
                // separate optional-dependency commands which follow. Never
                // clear this fence from a failed preparation: another
                // uninstaller may already have observed it as a retry and
                // begun its own cleanup. The next supported uninstall retry
                // owns completing and clearing the durable transaction.
                BackendLifecycleStateStore.BeginUninstallLocked(
                    operationLock);
            }
            else
            {
                // Upgrade/repair uses this same physical-safety probe but must
                // not proceed through an unfinished uninstall transaction or
                // race a backend/deferred-setup mutation. Keep the lifecycle
                // operation lock through the complete physical-safety probe.
                BackendLifecycleStateStore.RequireNoUninstallInProgress();
            }
            return RecoverPhysicalAndDiscardPendingTransaction(
                allowLegacyVddOwnershipMigration: true);
        }
        finally
        {
            operationLock.Dispose();
        }
    }

    private static void RequireOwnedFinalizationPreflight()
    {
        var rescueAgent = HostRecoveryAgentManager.GetInstallationState();
        var recovery = RecoveryTaskManager.GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(
            rescueAgent,
            HostRecoveryAgentManager.TaskName);
        ExactScheduledTaskManager.RequireKnown(
            recovery,
            RecoveryTaskManager.TaskName);
        if (rescueAgent.State == ExactScheduledTaskState.Present)
        {
            ExactScheduledTaskManager.RequireOwnedInteractiveTask(
                HostRecoveryAgentManager.TaskName,
                InstallationTrust.ExpectedExecutablePath,
                "agent run --background",
                requireInteractiveHighest: false);
        }
        if (recovery.State == ExactScheduledTaskState.Present)
        {
            ExactScheduledTaskManager.RequireOwnedInteractiveTask(
                RecoveryTaskManager.TaskName,
                InstallationTrust.ExpectedExecutablePath,
                "session recover",
                requireInteractiveHighest: false);
        }
        SunshineConfigurator.RequireManagedIntegrationCleanupReady();
    }

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransaction(
            bool allowLegacyVddOwnershipMigration = false)
    {
        return WithSunshineStopped(
            () =>
            {
                UninstallPreparationResult result;
                using (var transaction = DisplayTransactionLock.Acquire())
                {
                    if (allowLegacyVddOwnershipMigration)
                    {
                        ManagedVddOwnershipJournal
                            .MigrateLegacyOwnershipIfProvenLocked(
                                transaction);
                    }
                    result = RecoverPhysicalAndDiscardPendingTransactionLocked(
                        transaction);
                }
                MachineStateSecurity.SecureAfterLegacyMigration();
                return result;
            },
            "Display recovery");
    }

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionForRecoveryUpgradeOnly()
    {
        return WithSunshineStopped(
            () =>
            {
                UninstallPreparationResult result;
                using (var transaction = DisplayTransactionLock
                           .AcquireForRecoveryUpgradeOnly())
                {
                    ManagedVddOwnershipJournal
                        .MigrateLegacyOwnershipIfProvenLocked(
                            transaction);
                    result = RecoverPhysicalAndDiscardPendingTransactionLocked(
                        transaction);
                }
                MachineStateSecurity.SecureAfterLegacyMigration();
                return result;
            },
            "Legacy display recovery migration");
    }

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(
            int ownerProcessId,
            bool allowManagedVddRestart)
    {
        return WithSunshineStopped(
            () =>
            {
                UninstallPreparationResult result;
                using (var transaction = DisplayTransactionLock
                           .AcquireForInstallerMaintenanceBootstrap(
                               ownerProcessId))
                {
                    ManagedVddOwnershipJournal
                        .MigrateLegacyOwnershipIfProvenLocked(
                            transaction,
                            allowAuthorizedMaintenanceBootstrap: true);
                    result =
                        RecoverPhysicalAndDiscardPendingTransactionWithManagedVddBootstrapLocked(
                            transaction,
                            allowManagedVddRestart,
                            "Installer maintenance");
                }
                MachineStateSecurity.SecureAfterLegacyMigration();
                return result;
            },
            "Installer maintenance legacy display recovery");
    }

    /// <summary>
    /// Emergency recovery may use the same narrowly gated bridge as installer
    /// maintenance, but only after its caller has established that Vita host
    /// features are Enabled and stopped Sunshine if it was running.
    /// </summary>
    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionForEmergencyLocked(
            DisplayTransactionLease transaction) =>
        RecoverPhysicalAndDiscardPendingTransactionWithManagedVddBootstrapLocked(
            transaction,
            allowManagedVddRestart: true,
            "Emergency recovery");

    private static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionWithManagedVddBootstrapLocked(
            DisplayTransactionLease transaction,
            bool allowManagedVddRestart,
            string operationName)
    {
        transaction.RequireActive();
        PhysicalDisplayUnavailableException initialFailure;
        try
        {
            return RecoverPhysicalAndDiscardPendingTransactionLocked(
                transaction);
        }
        catch (PhysicalDisplayUnavailableException error)
        {
            initialFailure = error;
        }

        RequireExactManagedVddOnlyRecoveryTopology(operationName);
        Exception? rescanFailure = null;
        try
        {
            DisplayWizardAdapter.RescanDisplayDevicesForRecovery();
        }
        catch (Exception error) when (IsManagedVddBootstrapOperationalError(error))
        {
            rescanFailure = error;
        }

        if (TryRecoverPhysicalAfterBootstrapLocked(
                transaction,
                PhysicalRecoveryAttemptsAfterRescan,
                out var afterRescan,
                out _))
        {
            Console.WriteLine(
                $"{operationName} recovered the physical display after a Windows device rescan; no display device was restarted.");
            return afterRescan!;
        }

        if (!allowManagedVddRestart)
        {
            throw new InvalidOperationException(
                $"{operationName} found only the Vita virtual display. Windows device rescan did not restore a physical path, and Vita host features are Paused, so the managed display was not restarted or re-enabled. " +
                "Connect or enable a physical monitor, then retry. " +
                (rescanFailure is null
                    ? string.Empty
                    : "Device rescan response: " + rescanFailure.Message),
                rescanFailure ?? initialFailure);
        }

        // Re-read the topology immediately before the only disruptive bridge.
        // A physical or unrelated virtual path appearing after the rescan must
        // cancel the restart rather than broadening its target.
        RequireExactManagedVddOnlyRecoveryTopology(operationName);

        string? restartedInstance = null;
        Exception? restartFailure = null;
        try
        {
            restartedInstance =
                DisplayWizardAdapter.RestartExactManagedVddForRecovery(
                    transaction);
        }
        catch (HostRestartRequiredException)
        {
            throw;
        }
        catch (Exception error) when (IsManagedVddBootstrapOperationalError(error))
        {
            restartFailure = error;
        }

        if (TryRecoverPhysicalAfterBootstrapLocked(
                transaction,
                PhysicalRecoveryAttemptsAfterVddRestart,
                out var afterRestart,
                out var finalFailure))
        {
            Console.WriteLine(
                $"{operationName} restarted exact managed VDD {restartedInstance ?? "<restart returned an error>"} and then proved a physical-only display layout.");
            return afterRestart!;
        }

        var details = new List<string>
        {
            initialFailure.Message,
        };
        if (rescanFailure is not null)
        {
            details.Add("device rescan: " + rescanFailure.Message);
        }
        if (restartFailure is not null)
        {
            details.Add("exact managed VDD restart: " + restartFailure.Message);
        }
        if (finalFailure is not null)
        {
            details.Add("final physical-display proof: " + finalFailure.Message);
        }
        throw new InvalidOperationException(
            $"{operationName} could not prove a physical-only display layout after a Windows device rescan and a restart of only the exact managed Vita VDD. " +
            "No unrelated virtual display, shared driver package, scheduled task, or product file was changed. " +
            "The host and recovery state were kept for another safe retry. " +
            string.Join(" ", details),
            restartFailure ?? rescanFailure ?? finalFailure ?? initialFailure);
    }

    private static void RequireExactManagedVddOnlyRecoveryTopology(
        string operationName)
    {
        var selected = DisplayTopologyService
            .SelectExactManagedVddOnlyRecoveryPath(
                new DisplayTopologyService().ListDisplays());
        if (selected is null)
        {
            throw new InvalidOperationException(
                $"{operationName} did not restart any display device because Windows no longer exposes the exact one-managed-VDD-only recovery topology. " +
                "A physical display, another virtual-display product, no active path, or an ambiguous managed topology requires manual review.");
        }
    }

    private static bool TryRecoverPhysicalAfterBootstrapLocked(
        DisplayTransactionLease transaction,
        int attempts,
        out UninstallPreparationResult? result,
        out PhysicalDisplayUnavailableException? lastFailure)
    {
        result = null;
        lastFailure = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                result = RecoverPhysicalAndDiscardPendingTransactionLocked(
                    transaction);
                return true;
            }
            catch (PhysicalDisplayUnavailableException error)
            {
                lastFailure = error;
            }
            if (attempt < attempts - 1)
            {
                Thread.Sleep(PhysicalRecoveryRetryDelayMilliseconds);
            }
        }
        return false;
    }

    private static bool IsManagedVddBootstrapOperationalError(
        Exception error) =>
        error is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            TimeoutException or
            System.ComponentModel.Win32Exception;

    internal static UninstallPreparationResult
        RecoverPhysicalAndDiscardPendingTransactionLocked(
            DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        var topology = new DisplayTopologyService();
        IReadOnlyList<string> physicalDisplays;
        if (!File.Exists(ManagedVddOwnershipJournal.JournalFile) &&
            !File.Exists(HostStatePaths.RecoveryFile) &&
            topology.TryCaptureExactPhysicalOnlySnapshot(
                out var untouchedPhysical) &&
            untouchedPhysical is not null)
        {
            // Controller-only installs and unrelated DisplayWizard users may
            // legitimately have an inactive ROOT\MttVDD/IddSample device.
            // With no Vita ownership or recovery transaction, an already
            // exact physical-only desktop needs no mutation. This narrow path
            // lets setup/uninstall proceed without claiming, toggling, or
            // deactivating the third-party device; configured Vita backends
            // still require the journaled idle invariant below.
            physicalDisplays = untouchedPhysical.PhysicalDisplays;
        }
        else
        {
            var idle = ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                transaction,
                requireManagedDevice: false);
            physicalDisplays = idle.PhysicalDisplays;
        }

        // Recovery records from older installs lived in a replaceable
        // ProgramData child. Never inspect, traverse, or delete that untrusted
        // tree. Physical recovery above is authoritative and current versions
        // never read the legacy path.

        // Lock the new Program Files state container, then unlink any record
        // from an interrupted v2 setup without parsing or applying it.
        MachineStateSecurity.SecureContainer();
        var clearedSavedTransaction =
            SessionManager.DiscardPendingRecoveryLocked(transaction);
        if (!MachineStateSecurity.IsProtectionInitialized())
        {
            // Preserve no machine decisions from an incomplete migration.
            // Setup will recreate recommended settings in this protected
            // Program Files state root.
            TrustedFileSystem.DeleteFile(HostStatePaths.SettingsFile);
            TrustedFileSystem.DeleteFile(HostStatePaths.LastErrorFile);
            TrustedFileSystem.DeleteFile(
                DriverNativeModeVerification.VerificationFile);
            TrustedFileSystem.DeleteFile(
                DriverConfigurationDirectoryTrust.IdentityFile);
        }
        VerifyPhysicalOnlyTopology(topology);
        return new UninstallPreparationResult(
            physicalDisplays,
            clearedSavedTransaction);
    }

    internal static SunshineIntegrationCleanupResult CleanupIntegration() =>
        WithSunshineStopped(
            SunshineConfigurator.RemoveManagedIntegration,
            "Sunshine integration cleanup");

    /// <summary>
    /// Performs the irreversible, Vita-owned portion of uninstall only after
    /// recovering a physical-only topology a second time. If task removal or
    /// state cleanup fails, safeguards that existed on entry are restored and
    /// the physical topology is recovered again before the error is returned.
    /// Shared Sunshine, ViGEmBus, and VDD installations are never touched.
    /// </summary>
    internal static UninstallFinalizationResult FinalizeOwnedState(
        bool restoreSunshine,
        bool restoreManagedVdd)
    {
        // Persist this boundary before any finalization work. Enable/Disable
        // acquire the same backend lock and reject this marker, so no user
        // lifecycle operation can interleave with task removal, VDD handoff,
        // or owned-state cleanup. A successful uninstall deliberately leaves
        // the marker for the Inno uninstaller to remove as its exact final
        // state-file action.
        BackendLifecycleStateStore.BeginUninstall();
        var rescueAgentWasInstalled = false;
        var rescueAgentWasRunning = false;
        var recoveryTaskWasInstalled = false;
        BackendPersistedState? disabledBackendRollbackState = null;
        var rescueAgentRemoved = false;
        var recoveryTaskRemoved = false;
        var integrationCleanupStarted = false;
        var warnings = new List<string>();

        try
        {
            var finalRecovery = WithSunshineStopped(
                () =>
                {
                    using var transaction = DisplayTransactionLock.Acquire();
                    var recovery =
                        RecoverPhysicalAndDiscardPendingTransactionLocked(
                            transaction);
                    var backendHandoff =
                        BackendLifecycleManager.PrepareForUninstall(
                            transaction,
                            restoreSunshine,
                            restoreManagedVdd);
                    // Keep the exact pre-handoff snapshot in process memory.
                    // State cleanup may unlink both redundant files before a
                    // later filesystem failure is observed.
                    disabledBackendRollbackState =
                        backendHandoff.DisabledRollbackState;

                    if (restoreManagedVdd)
                    {
                        var restartRequired = DisplayWizardAdapter
                            .LocateBundledForUninstall()
                            .UninstallDriver(transaction);
                        if (restartRequired)
                        {
                            throw new HostRestartRequiredException(
                                "Windows must restart to finish removing the exact app-created virtual-display instance. The shared MttVDD package and retry-safe ownership journal were retained.");
                        }
                    }

                    VerifyPhysicalOnlyTopology(
                        new DisplayTopologyService());

                    try
                    {
                        var audio = AudioEndpointRecoveryService.RestorePending(
                            waitForEndpoint: true);
                        if (!audio.Succeeded)
                        {
                            var warning =
                                "Windows did not re-enumerate every exact pre-stream audio endpoint before the bounded uninstall attempt ended. Uninstall continued without guessing another output; Windows kept its current default audio choice. " +
                                (audio.Detail ?? string.Empty);
                            warnings.Add(warning.Trim());
                            Console.Error.WriteLine(warning);
                        }
                    }
                    catch (Exception audioError) when (
                        audioError is IOException or
                            UnauthorizedAccessException or
                            InvalidDataException or
                            InvalidOperationException or
                            ArgumentException or
                            System.ComponentModel.Win32Exception or
                            System.Security.SecurityException)
                    {
                        var warning =
                            "Windows audio recovery state could not be inspected during uninstall. Uninstall continued because supplementary audio state never authorizes display, task, driver, or file-retention decisions; Windows kept its current default audio choice. " +
                            audioError.Message;
                        warnings.Add(warning);
                        Console.Error.WriteLine(warning);
                    }

                    var rescueAgentBefore =
                        HostRecoveryAgentManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        rescueAgentBefore,
                        HostRecoveryAgentManager.TaskName);
                    rescueAgentWasInstalled =
                        rescueAgentBefore.State ==
                        ExactScheduledTaskState.Present;
                    rescueAgentWasRunning =
                        HostRecoveryAgentManager.IsRunning();
                    var recoveryTaskBefore =
                        RecoveryTaskManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        recoveryTaskBefore,
                        RecoveryTaskManager.TaskName);
                    recoveryTaskWasInstalled =
                        recoveryTaskBefore.State ==
                        ExactScheduledTaskState.Present;

                    HostRecoveryAgentManager.Uninstall();
                    rescueAgentRemoved =
                        rescueAgentWasInstalled || rescueAgentWasRunning;
                    var rescueAgentAfter =
                        HostRecoveryAgentManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        rescueAgentAfter,
                        HostRecoveryAgentManager.TaskName);
                    if (rescueAgentAfter.State ==
                            ExactScheduledTaskState.Present ||
                        HostRecoveryAgentManager.IsRunning())
                    {
                        throw new InvalidOperationException(
                            "Windows retained the Vita Moonlight stream-rescue " +
                            "task or background agent after its removal command.");
                    }

                    RecoveryTaskManager.Uninstall();
                    recoveryTaskRemoved = recoveryTaskWasInstalled;
                    var recoveryTaskAfter =
                        RecoveryTaskManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        recoveryTaskAfter,
                        RecoveryTaskManager.TaskName);
                    if (recoveryTaskAfter.State ==
                        ExactScheduledTaskState.Present)
                    {
                        throw new InvalidOperationException(
                            "Windows retained the Vita Moonlight display-recovery " +
                            "task after its removal command.");
                    }

                    // Hold the display lease through task removal and the final
                    // topology proof so a new session cannot begin between
                    // those two safety boundaries. Restore Vita-owned
                    // Sunshine integration while Sunshine is still stopped;
                    // otherwise its live process can retain hooks which point
                    // at the host executable after Inno removes it.
                    VerifyPhysicalOnlyTopology(
                        new DisplayTopologyService());
                    // Cleanup is restartable across more than one streaming-
                    // host file. Once the first call begins, any later failure
                    // is conservatively post-commit; never recreate safeguards
                    // or clear the uninstall fence against a partial cleanup.
                    integrationCleanupStarted = true;
                    // Always remove exact owned hooks/settings, even when the
                    // user separately removed Sunshine. The journal may also
                    // contain an older Apollo location which remains installed.
                    SunshineConfigurator.RemoveManagedIntegration();
                    return recovery;
                },
                "Uninstall finalization display recovery");

            OwnedStateCleanupResult stateCleanup;
            try
            {
                // Exact state deletion is deliberately best-effort after the
                // integration commit. Unknown, locked, or untrusted entries
                // are retained and reported; they must not turn a completed
                // safe uninstall into an irreversible rollback problem.
                stateCleanup = CleanupOwnedStateFiles();
            }
            catch (Exception cleanupError) when (
                cleanupError is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    System.ComponentModel.Win32Exception)
            {
                stateCleanup = new OwnedStateCleanupResult(
                    0,
                    0,
                    [
                        HostStatePaths.Root +
                        " (cleanup retained after " +
                        cleanupError.GetType().Name + ")",
                    ]);
            }
            // This is the final host-owned commit point. Inno keeps the
            // primary executable until this exact stage is durable, then
            // verifies its deletion and removes this finalized guard as the
            // last exact state-file action. A retry can therefore distinguish
            // a safely committed file-only cleanup from incomplete rollback.
            BackendLifecycleStateStore.MarkUninstallFinalized();
            return new UninstallFinalizationResult(
                finalRecovery.PhysicalDisplays,
                rescueAgentRemoved,
                recoveryTaskRemoved,
                stateCleanup,
                warnings);
        }
        catch (Exception error) when (integrationCleanupStarted)
        {
            // Vita-owned integration has already been removed and its
            // ownership journal committed. Recreating tasks or a paused VDD
            // snapshot would pretend that irreversible configuration work was
            // rolled back. Keep the uninstall fence and exact owned payload
            // for a safe retry instead.
            throw new InvalidOperationException(
                "Vita-owned Sunshine integration was removed, but uninstall " +
                "could not finish or durably commit finalization. " +
                "The uninstall transaction remains protected; retry uninstall.",
                error);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            var executablePath = Environment.ProcessPath ??
                Path.Combine(
                    AppContext.BaseDirectory,
                    "VitaMoonlight.Host.exe");

            if (disabledBackendRollbackState is not null)
            {
                try
                {
                    var rollbackState = disabledBackendRollbackState;
                    using (var displayTransaction =
                           DisplayTransactionLock.Acquire())
                    {
                        var ownershipRollback = ManagedVddOwnershipJournal
                            .ReactivateReleasedOwnershipLocked(
                                displayTransaction);
                        if (ownershipRollback ==
                            ManagedVddRollbackDisposition.RetainedTombstone)
                        {
                            // An already-removed app-created node or a node
                            // relinquished to preserve a concurrent change
                            // cannot be guessed back into ownership. Restore
                            // the remaining paused safeguards without later
                            // authorizing a toggle of that exact ID.
                            rollbackState = rollbackState with
                            {
                                ManagedVirtualDisplayInstancesToRestore = [],
                            };
                        }
                    }
                    BackendLifecycleManager
                        .RestoreDisabledStateAfterFailedUninstall(
                            rollbackState);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(new InvalidOperationException(
                        "The explicitly paused Vita host features could not be returned " +
                        "to its disabled state after uninstall failed.",
                        rollbackError));
                }
            }

            if (recoveryTaskWasInstalled)
            {
                try
                {
                    var recoveryTaskNow =
                        RecoveryTaskManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        recoveryTaskNow,
                        RecoveryTaskManager.TaskName);
                    if (recoveryTaskNow.State ==
                        ExactScheduledTaskState.Missing)
                    {
                        RecoveryTaskManager.Install(executablePath);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(new InvalidOperationException(
                        "The display-recovery task could not be restored " +
                        "after uninstall finalization failed.",
                        rollbackError));
                }
            }

            if (rescueAgentWasInstalled || rescueAgentWasRunning)
            {
                try
                {
                    var rescueAgentNow =
                        HostRecoveryAgentManager.GetInstallationState();
                    ExactScheduledTaskManager.RequireKnown(
                        rescueAgentNow,
                        HostRecoveryAgentManager.TaskName);
                    if (rescueAgentNow.State ==
                            ExactScheduledTaskState.Missing ||
                        !HostRecoveryAgentManager.IsRunning())
                    {
                        HostRecoveryAgentManager.Install(executablePath);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(new InvalidOperationException(
                        "The stream-rescue agent could not be restored " +
                        "after uninstall finalization failed.",
                        rollbackError));
                }
            }

            try
            {
                RecoverPhysicalAndDiscardPendingTransaction();
            }
            catch (Exception rollbackError)
            {
                rollbackErrors.Add(new InvalidOperationException(
                    "The physical display could not be reverified after " +
                    "uninstall finalization failed.",
                    rollbackError));
            }

            // Clear the transaction fence only after every rollback action
            // succeeded. If any rollback is incomplete, the durable marker
            // must continue blocking Enable/Disable and new sessions until a
            // safe uninstall retry finishes the transaction.
            if (rollbackErrors.Count == 0)
            {
                try
                {
                    BackendLifecycleStateStore.CancelUninstall();
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(new InvalidOperationException(
                        "The uninstall transaction guard could not be cleared " +
                        "after rollback. Retry uninstall before changing host features.",
                        rollbackError));
                }
            }

            if (rollbackErrors.Count == 0)
            {
                throw new InvalidOperationException(
                    "Uninstall finalization failed. The physical display " +
                    "was recovered and the pre-existing Vita Moonlight " +
                    "recovery safeguards were restored.",
                    error);
            }

            throw new AggregateException(
                "Uninstall finalization failed, and one or more recovery " +
                "rollback actions also failed. Restart Windows before " +
                "retrying uninstall.",
                new[] { error }.Concat(rollbackErrors));
        }
    }

    internal static OwnedStateCleanupResult CleanupOwnedStateFiles()
    {
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            "VitaMoonlight");
        var state = CleanupOwnedStateFiles(
            HostStatePaths.Root,
            legacyRoot);
        var residue = InstallResidueCleanup
            .CleanupObsoleteRootPayloadForUninstall(
                InstallationTrust.ExpectedInstallationDirectory);
        return new OwnedStateCleanupResult(
            state.RemovedFiles + residue.RemovedFiles,
            state.RemovedDirectories + residue.RemovedDirectories,
            state.RetainedEntries
                .Concat(residue.RetainedOwnedEntries)
                .Concat(residue.RetainedDirectories)
                .Distinct(OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
                .ToArray());
    }

    internal static OwnedStateCleanupResult CleanupOwnedStateFiles(
        string currentRoot,
        string legacyRoot)
    {
        currentRoot = Path.GetFullPath(currentRoot);
        legacyRoot = Path.GetFullPath(legacyRoot);
        var removedFiles = 0;
        var removedDirectories = 0;
        var retainedEntries = new List<string>();

        CleanupKnownDirectory(
            Path.Combine(currentRoot, "Diagnostics"),
            DiagnosticStateFiles,
            retainedEntries,
            ref removedFiles,
            ref removedDirectories,
            failOnUntrustedDirectory: false);
        CleanupKnownDirectory(
            currentRoot,
            CurrentRootStateFiles,
            retainedEntries,
            ref removedFiles,
            ref removedDirectories,
            failOnUntrustedDirectory: false);

        if (!PathsEqual(legacyRoot, currentRoot))
        {
            var legacy = InstallResidueCleanup
                .CleanupLegacyProgramDataForUninstall(legacyRoot);
            removedFiles += legacy.RemovedFiles;
            removedDirectories += legacy.RemovedDirectories;
            retainedEntries.AddRange(legacy.RetainedOwnedEntries);
            retainedEntries.AddRange(legacy.RetainedDirectories);
        }

        return new OwnedStateCleanupResult(
            removedFiles,
            removedDirectories,
            retainedEntries);
    }

    private static void CleanupKnownDirectory(
        string directoryPath,
        IReadOnlyList<string> ownedFileNames,
        ICollection<string> retainedEntries,
        ref int removedFiles,
        ref int removedDirectories,
        bool failOnUntrustedDirectory)
    {
        var fullPath = Path.GetFullPath(directoryPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(fullPath)) return;

        if (!OperatingSystem.IsWindows())
        {
            foreach (var fileName in ownedFileNames)
            {
                var candidate = Path.Combine(fullPath, fileName);
                if (TrustedFileSystem.DeleteFile(candidate)) removedFiles++;
            }
            if (!Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                Directory.Delete(fullPath);
                removedDirectories++;
            }
            else
            {
                retainedEntries.Add(fullPath);
            }
            return;
        }

        // Holding a no-follow directory handle without FILE_SHARE_DELETE
        // prevents replacement of the directory pathname while exact child
        // entries are unlinked. Reparse directories are rejected by Inspect.
        try
        {
            using var directory =
                TrustedFileSystem.AcquireDirectoryLease(fullPath);
            foreach (var fileName in ownedFileNames)
            {
                var candidate = Path.Combine(fullPath, fileName);
                try
                {
                    if (TrustedFileSystem.DeleteFile(candidate))
                    {
                        removedFiles++;
                    }
                }
                catch (Exception error) when (
                    !failOnUntrustedDirectory &&
                    error is IOException or
                        UnauthorizedAccessException or
                        InvalidDataException or
                        System.ComponentModel.Win32Exception)
                {
                    // One briefly locked owned file must not prevent cleanup
                    // of every later allowlisted entry. The installer gets a
                    // final exact deletion pass after host children exit.
                    retainedEntries.Add(candidate);
                }
            }

            if (Directory.EnumerateFileSystemEntries(fullPath).Any())
            {
                retainedEntries.Add(fullPath);
                return;
            }
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception error) when (
            !failOnUntrustedDirectory &&
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            // Old ProgramData state predates the protected state container.
            // Never let a replaceable/reparse legacy entry redirect elevated
            // cleanup, and do not make that unrelated unsafe object a blocker
            // for uninstalling the protected current product.
            retainedEntries.Add(fullPath);
            return;
        }

        // The directory was proven empty while its stable handle was held.
        // A nonrecursive delete cannot consume contents of a replacement; it
        // either removes the same empty entry/link or safely fails.
        try
        {
            Directory.Delete(fullPath, recursive: false);
            removedDirectories++;
        }
        catch (DirectoryNotFoundException)
        {
            // Another trusted cleanup already completed the idempotent step.
        }
        catch (IOException)
        {
            retainedEntries.Add(fullPath);
        }
        catch (Exception error) when (
            error is UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            retainedEntries.Add(fullPath);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    internal static void VerifyPhysicalOnlyTopology(DisplayTopologyService topology)
    {
        var displays = topology.ListDisplays();
        var activePhysical = displays.Where(display =>
            display.IsActive &&
            display.IsAvailable &&
            !DisplayTopologyService.IsLikelyVirtualDisplay(display)).ToArray();
        if (activePhysical.Length == 0)
        {
            throw new InvalidOperationException(
                "Windows did not confirm an active physical display. " +
                "Uninstall will keep the host and its recovery safeguards installed.");
        }

        var activeManagedVirtual = displays.Where(display =>
            display.IsActive &&
            DisplayTopologyService.IsManagedVirtualDisplay(display)).ToArray();
        if (activeManagedVirtual.Length > 0)
        {
            throw new InvalidOperationException(
                "Windows still reports the Vita virtual display as active. " +
                "Uninstall will keep the host and its recovery safeguards installed.");
        }
    }

    private static T WithSunshineStopped<T>(
        Func<T> operation,
        string operationName)
    {
        var serviceName = StreamingHostLocator.FindSunshineServiceName();
        var initialSunshineState =
            WindowsServiceManager.GetState(serviceName);
        var stopSunshine = initialSunshineState is not (
            WindowsServiceState.NotInstalled or
            WindowsServiceState.Stopped);
        var restartSunshine = initialSunshineState is
            WindowsServiceState.Running or
            WindowsServiceState.StartPending or
            WindowsServiceState.ContinuePending or
            WindowsServiceState.PausePending or
            WindowsServiceState.Paused;
        Exception? operationError = null;

        if (stopSunshine)
        {
            WindowsServiceManager.Stop(serviceName, "Sunshine");
        }

        try
        {
            return operation();
        }
        catch (Exception error)
        {
            operationError = error;
            throw;
        }
        finally
        {
            if (restartSunshine &&
                WindowsServiceManager.GetState(serviceName) !=
                WindowsServiceState.NotInstalled)
            {
                try
                {
                    WindowsServiceManager.Start(serviceName, "Sunshine");
                }
                catch (Exception restartError)
                {
                    if (operationError is null)
                    {
                        throw new InvalidOperationException(
                            $"{operationName} finished, but Sunshine could not be restarted. " +
                            restartError.Message,
                            restartError);
                    }
                    throw new AggregateException(
                        $"{operationName} failed and Sunshine could not be restarted.",
                        operationError,
                        restartError);
                }
            }
        }
    }
}
