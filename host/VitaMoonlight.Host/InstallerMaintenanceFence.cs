using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record InstallerMaintenanceState(
    int FormatVersion,
    int OwnerProcessId,
    DateTimeOffset OwnerStartedAtUtc,
    DateTimeOffset BeganAtUtc,
    bool BackendWasEnabled,
    bool RescueAgentTaskWasPresent,
    bool RecoveryTaskWasPresent,
    string? StagedVddAdoptionInstanceId = null,
    long Revision = 0);

internal sealed record InstallerMaintenanceStatus(
    InstallerMaintenanceState State,
    bool OwnerIsAlive);

/// <summary>
/// Serializes every host mutation with setup and leaves a durable, live-owner
/// fence across the separate child processes used by Inno Setup. Read-only
/// diagnostics remain available while setup owns the fence. Only an installed
/// host command carrying the exact live setup PID receives the maintenance
/// bypass; a stale fence can be taken over only after its recorded process has
/// ended.
/// </summary>
internal static class InstallerMaintenanceFence
{
    // Keep the wire format at v2: an already-installed <=0.14.8 host must be
    // able to read the fence before setup replaces that executable. The
    // candidate and revision are additive JSON members, which the old
    // System.Text.Json reader ignores. A legacy v2 record therefore has
    // revision zero; current publications use revision one or later.
    private const int CurrentFormatVersion = 2;
    private const int LegacyFormatVersion = 1;
    private const int MaximumStateBytes = 16 * 1024;
    // Installed setup children borrow the live fence with this option. The
    // maintenance controller deliberately uses the separate `--owner-pid`
    // option and performs its own gate/live-owner checks in Begin, End, and
    // GetSnapshotForOwner. This keeps stale takeover reachable without a
    // second acquisition of the exclusive command gate.
    private const string OwnerOption = "--maintenance-owner-pid";
    private static readonly AsyncLocal<int> OwnedCommandDepth = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string StateFile => Path.Combine(
        HostStatePaths.Root,
        "installer-maintenance.json");

    internal static string BackupFile => Path.Combine(
        HostStatePaths.Root,
        "installer-maintenance.backup.json");

    internal static string CommandGateFile => Path.Combine(
        HostStatePaths.Root,
        "installer-maintenance.lock");

    internal static bool IsPresent =>
        File.Exists(StateFile) || File.Exists(BackupFile);

    internal static bool CurrentCommandOwnsFence =>
        OwnedCommandDepth.Value > 0;

    internal static InstallerMaintenanceState Begin(int ownerProcessId)
    {
        RequireControllerProcess();
        ScheduledTaskAccount.RequireCurrentInteractiveUser(
            "Vita Moonlight setup or repair");
        var ownerStart = RequireLiveProcessStart(ownerProcessId);
        MachineStateSecurity.SecureContainer();
        using var commandGate = AcquireCommandGate();
        var bootstrapFirst = MustBootstrapBeforeProtectedState(
            MachineStateSecurity.IsProtectionInitialized());
        InstallerMaintenanceState? existing = null;

        if (!bootstrapFirst)
        {
            MachineStateSecurity.Secure();
            existing = LoadForBegin();
            if (CanReuseLiveOwner(
                    existing,
                    ownerProcessId,
                    ownerStart))
            {
                return existing!;
            }
        }

        if (bootstrapFirst)
        {
            // Pre-v2 installations cannot call Secure or acquire the backend
            // lock yet: both deliberately reject state until the typed
            // physical-only migration initializes protection. No supported
            // legacy build can own a new-format maintenance fence, so perform
            // the bootstrap under the installer command/display gates first,
            // then inspect any records in the newly protected container.
            UninstallManager
                .RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(
                    ownerProcessId,
                    ResolveManagedVddBootstrapPermission(existing: null));
            MachineStateSecurity.Secure();
            existing = LoadForBegin();
            if (CanReuseLiveOwner(
                    existing,
                    ownerProcessId,
                    ownerStart))
            {
                return existing!;
            }
        }

        using var backendOperation = BackendLifecycleStateStore.AcquireLock();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        if (!bootstrapFirst)
        {
            // Current protected installs reject another live owner before this
            // recovery. Holding the backend lease also prevents a lifecycle or
            // uninstall transition between the proof and fence publication.
            UninstallManager
                .RecoverPhysicalAndDiscardPendingTransactionForInstallerMaintenanceBootstrap(
                    ownerProcessId,
                    ResolveManagedVddBootstrapPermission(existing));
            MachineStateSecurity.Secure();
        }

        var snapshot = existing is null
            ? CaptureCurrentSnapshot()
            : NormalizeSnapshotForTakeover(existing);

        var state = new InstallerMaintenanceState(
            CurrentFormatVersion,
            ownerProcessId,
            ownerStart,
            DateTimeOffset.UtcNow,
            snapshot.BackendWasEnabled,
            snapshot.RescueAgentTaskWasPresent,
            snapshot.RecoveryTaskWasPresent,
            StagedVddAdoptionInstanceId: null,
            Revision: 1);
        Validate(state);
        var serialized = JsonSerializer.Serialize(state, JsonOptions);
        // The backup becomes durable before the public primary. Therefore a
        // crash during first publication leaves either no valid state (which
        // Begin can identify as pre-publication) or at least one complete,
        // live-owner record. An existing fence is never destroyed while its
        // replacement is being published.
        TrustedFileSystem.WriteAllText(BackupFile, serialized);
        TrustedFileSystem.WriteAllText(StateFile, serialized);
        return state;
    }

    internal static bool End(int ownerProcessId)
    {
        RequireControllerProcess();
        var ownerStart = RequireLiveProcessStart(ownerProcessId);
        MachineStateSecurity.Secure();
        using var commandGate = AcquireCommandGate();
        using var backendOperation = BackendLifecycleStateStore.AcquireLock();
        var existing = Load();
        if (existing is null) return false;
        if (existing.OwnerProcessId != ownerProcessId ||
            existing.OwnerStartedAtUtc.UtcTicks != ownerStart.UtcTicks)
        {
            throw new InvalidOperationException(
                $"Process {ownerProcessId} does not own the active Vita Moonlight installer-maintenance fence. " +
                $"The recorded owner is process {existing.OwnerProcessId}.");
        }
        VerifySafeToEnd(existing);
        TrustedFileSystem.DeleteFile(StateFile);
        TrustedFileSystem.DeleteFile(BackupFile);
        return true;
    }

    internal static InstallerMaintenanceState GetSnapshotForOwner(
        int ownerProcessId)
    {
        RequireControllerProcess();
        var ownerStart = RequireLiveProcessStart(ownerProcessId);
        MachineStateSecurity.Secure();
        using var commandGate = AcquireCommandGate();
        var state = Load() ?? throw new InvalidOperationException(
            "No protected installer-maintenance snapshot is active.");
        if (state.OwnerProcessId != ownerProcessId ||
            state.OwnerStartedAtUtc.UtcTicks != ownerStart.UtcTicks)
        {
            throw new InvalidOperationException(
                $"Process {ownerProcessId} does not own the active Vita Moonlight installer-maintenance snapshot.");
        }
        if (state.FormatVersion == CurrentFormatVersion) return state;
        using var backendOperation = BackendLifecycleStateStore.AcquireLock();
        var snapshot = NormalizeSnapshotForTakeover(state);
        return state with
        {
            FormatVersion = CurrentFormatVersion,
            BackendWasEnabled = snapshot.BackendWasEnabled,
            RescueAgentTaskWasPresent = snapshot.RescueAgentTaskWasPresent,
            RecoveryTaskWasPresent = snapshot.RecoveryTaskWasPresent,
            StagedVddAdoptionInstanceId = null,
            Revision = 1,
        };
    }

    /// <summary>
    /// Binds the installer's later consent decision to the exact device which
    /// was visible when the question was prepared. This is not ownership and
    /// authorizes no PnP mutation. The staged identity lives only inside the
    /// live-owner maintenance fence and disappears when setup ends.
    /// </summary>
    internal static InstallerMaintenanceState StageVddAdoptionCandidateForOwner(
        int ownerProcessId,
        string? candidateInstanceId)
    {
        RequireControllerProcess();
        var ownerStart = RequireLiveProcessStart(ownerProcessId);
        MachineStateSecurity.Secure();
        using var commandGate = AcquireCommandGate();
        var state = RequireOwnedSnapshot(
            ownerProcessId,
            ownerStart);
        if (candidateInstanceId is not null &&
            !ManagedVddOwnershipJournal.IsValidInstanceIdForAdoption(
                candidateInstanceId))
        {
            throw new InvalidDataException(
                "Windows returned an invalid managed-VDD adoption candidate identity.");
        }
        if (string.Equals(
                state.StagedVddAdoptionInstanceId,
                candidateInstanceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return state;
        }
        if (state.FormatVersion != CurrentFormatVersion)
        {
            var snapshot = NormalizeSnapshotForTakeover(state);
            state = state with
            {
                FormatVersion = CurrentFormatVersion,
                BackendWasEnabled = snapshot.BackendWasEnabled,
                RescueAgentTaskWasPresent =
                    snapshot.RescueAgentTaskWasPresent,
                RecoveryTaskWasPresent =
                    snapshot.RecoveryTaskWasPresent,
                StagedVddAdoptionInstanceId = null,
                Revision = 1,
            };
        }
        var updated = state with
        {
            StagedVddAdoptionInstanceId = candidateInstanceId,
            Revision = checked(state.Revision + 1),
        };
        Validate(updated);
        var serialized = JsonSerializer.Serialize(updated, JsonOptions);
        TrustedFileSystem.WriteAllText(BackupFile, serialized);
        TrustedFileSystem.WriteAllText(StateFile, serialized);
        return updated;
    }

    internal static string RequireStagedVddAdoptionCandidateForOwner(
        int ownerProcessId)
    {
        RequireControllerProcess();
        var ownerStart = RequireLiveProcessStart(ownerProcessId);
        MachineStateSecurity.Secure();
        InstallerMaintenanceState state;
        if (CurrentCommandOwnsFence)
        {
            state = RequireOwnedSnapshot(
                ownerProcessId,
                ownerStart);
        }
        else
        {
            using var commandGate = AcquireCommandGate();
            state = RequireOwnedSnapshot(
                ownerProcessId,
                ownerStart);
        }
        return state.StagedVddAdoptionInstanceId
            ?? throw new InvalidOperationException(
                "Setup has no exact staged virtual-display adoption candidate. Return to the setup choices and approve the current device again.");
    }

    private static InstallerMaintenanceState RequireOwnedSnapshot(
        int ownerProcessId,
        DateTimeOffset ownerStart)
    {
        var state = Load() ?? throw new InvalidOperationException(
            "No protected installer-maintenance snapshot is active.");
        if (state.OwnerProcessId != ownerProcessId ||
            state.OwnerStartedAtUtc.UtcTicks != ownerStart.UtcTicks)
        {
            throw new InvalidOperationException(
                $"Process {ownerProcessId} does not own the active Vita Moonlight installer-maintenance snapshot.");
        }
        return state;
    }

    internal static InstallerMaintenanceStatus? Inspect()
    {
        var state = Load();
        return state is null
            ? null
            : new InstallerMaintenanceStatus(
                state,
                IsRecordedOwnerAlive(state));
    }

    internal static IDisposable AuthorizeCommand(
        string command,
        IReadOnlyList<string> args)
    {
        var mutating = IsMutatingCommand(command, args);
        var suppliedOwner = GetOwnerProcessId(args);
        if (!RequiresSerializedAccess(
                mutating,
                IsPresent,
                suppliedOwner))
        {
            return EmptyLease.Instance;
        }

        MachineStateSecurity.SecureContainer();
        var commandGate = AcquireCommandGate();
        try
        {
            var state = Load();
            if (state is null)
            {
                if (mutating)
                {
                    return new CommandAccessLease(
                        commandGate,
                        ownsFence: false);
                }
                commandGate.Dispose();
                return EmptyLease.Instance;
            }

            if (IsPhysicalRecoveryCommand(command, args))
            {
                // Exact session recovery is allowed without owner authority,
                // but still holds the command gate so it cannot overlap an
                // active installer child. This is the sign-in/emergency path
                // which repairs a physical desktop after setup itself dies.
                return new CommandAccessLease(
                    commandGate,
                    ownsFence: false);
            }

            if (IsDeadOwnerUninstallBridge(command, args) &&
                !IsRecordedOwnerAlive(state))
            {
                InstallationTrust.RequireInstalledPayload(
                    "Interrupted-setup uninstall recovery");
                using (var backendOperation =
                       BackendLifecycleStateStore.AcquireLock())
                {
                    // Publish the durable uninstall guard before removing
                    // either redundant maintenance record. A crash at any
                    // point therefore leaves at least one fence which blocks
                    // user mutations; a retry can repeat this exact bridge.
                    BackendLifecycleStateStore.BeginUninstallLocked(
                        backendOperation);
                    TrustedFileSystem.DeleteFile(StateFile);
                    TrustedFileSystem.DeleteFile(BackupFile);
                }
                return new CommandAccessLease(
                    commandGate,
                    ownsFence: false);
            }

            if (suppliedOwner is null ||
                suppliedOwner.Value != state.OwnerProcessId ||
                !IsRecordedOwnerAlive(state))
            {
                throw new InvalidOperationException(
                    "Vita Moonlight setup or repair is in progress. " +
                    "Finish it before changing host features or starting a Vita session. " +
                    "If setup was interrupted, run the installer again so it can safely take over and finish recovery.");
            }

            InstallationTrust.RequireInstalledPayload(
                "Installer maintenance access");
            return new CommandAccessLease(
                commandGate,
                ownsFence: true);
        }
        catch
        {
            commandGate.Dispose();
            throw;
        }
    }

    internal static bool IsMutatingCommand(
        string command,
        IReadOnlyList<string> args)
    {
        command = command.Trim().ToLowerInvariant();
        var action = args.FirstOrDefault()?.Trim().ToLowerInvariant();
        return command switch
        {
            "gui" or "doctor" or "profile" or "support" or
                "self-test" or "help" or "--help" or "-h" or
                "maintenance" => false,
            "host" or "gamepad" or "runtime" or "driver" =>
                action is not "status",
            "display" => action is not null && action != "list",
            "backend" or "deferred-setup" or "session" =>
                action is not "status",
            "recovery" => action is not "status" and not "task-status",
            // The scheduled rescue process must be able to start while setup
            // owns the fence. It observes IsEnabled=false and remains in
            // physical-recovery-only mode until the exact owner ends setup.
            "agent" => action is not "status" and not "task-status" and not "run",
            _ => true,
        };
    }

    internal static bool RequiresSerializedAccessForTest(
        string command,
        IReadOnlyList<string> args,
        bool fencePresent)
    {
        var suppliedOwner = GetOwnerProcessId(args);
        return RequiresSerializedAccess(
            IsMutatingCommand(command, args),
            fencePresent,
            suppliedOwner);
    }

    internal static bool IsOwnerlessPhysicalRecoveryForTest(
        string command,
        IReadOnlyList<string> args) =>
        IsPhysicalRecoveryCommand(command, args);

    private static bool RequiresSerializedAccess(
        bool mutating,
        bool fencePresent,
        int? suppliedOwner) =>
        mutating || fencePresent && suppliedOwner is not null;

    private static InstallerMaintenanceState? Load()
    {
        var primary = TryRead(StateFile);
        var backup = TryRead(BackupFile);
        return SelectNewestValidState(
            primary.State,
            backup.State,
            primary.Exists || backup.Exists);
    }

    internal static InstallerMaintenanceState? SelectNewestValidStateForTest(
        InstallerMaintenanceState? primary,
        InstallerMaintenanceState? backup,
        bool anyRecordExists) =>
        SelectNewestValidState(primary, backup, anyRecordExists);

    internal static bool CanRepairInterruptedFirstPublicationForTest(
        bool primaryExists,
        InstallerMaintenanceState? primary,
        bool backupExists,
        InstallerMaintenanceState? backup) =>
        (primaryExists || backupExists) &&
        primary is null &&
        backup is null;

    internal static bool IsValidStateForTest(
        InstallerMaintenanceState state)
    {
        try
        {
            Validate(state);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static InstallerMaintenanceState? SelectNewestValidState(
        InstallerMaintenanceState? primary,
        InstallerMaintenanceState? backup,
        bool anyRecordExists)
    {
        if (primary is null && backup is null)
        {
            if (!anyRecordExists) return null;
            throw new InvalidDataException(
                "Both protected installer-maintenance fence records are unreadable. " +
                "Run setup again to recover it safely.");
        }
        if (primary is not null &&
            backup is not null &&
            primary.BeganAtUtc == backup.BeganAtUtc)
        {
            if (primary.OwnerProcessId != backup.OwnerProcessId ||
                primary.OwnerStartedAtUtc.UtcTicks !=
                    backup.OwnerStartedAtUtc.UtcTicks ||
                primary.BackendWasEnabled != backup.BackendWasEnabled ||
                primary.RescueAgentTaskWasPresent !=
                    backup.RescueAgentTaskWasPresent ||
                primary.RecoveryTaskWasPresent !=
                    backup.RecoveryTaskWasPresent)
            {
                throw new InvalidDataException(
                    "The protected installer-maintenance fence records conflict on their transaction owner or rollback baseline. Run setup again to recover them safely.");
            }
            if (primary.Revision == backup.Revision &&
                primary != backup)
            {
                throw new InvalidDataException(
                    "The protected installer-maintenance fence records conflict at the same revision. Run setup again to recover them safely.");
            }
            return primary.Revision >= backup.Revision
                ? primary
                : backup;
        }
        return new[] { primary, backup }
            .Where(candidate => candidate is not null)
            .OrderByDescending(candidate => candidate!.BeganAtUtc)
            .First();
    }

    private static InstallerMaintenanceState? LoadForBegin()
    {
        try
        {
            return Load();
        }
        catch (InvalidDataException)
        {
            var primary = TryRead(StateFile);
            var backup = TryRead(BackupFile);
            if (primary.State is not null || backup.State is not null)
            {
                throw;
            }

            // With backup-first publication, two absent/unreadable records can
            // only be an interrupted first publication (or invalid data which
            // never established a verifiable owner). The command gate and
            // backend lease are held, so deleting these exact files cannot
            // race a live owner. A later valid publication is never guessed.
            TrustedFileSystem.DeleteFile(StateFile);
            TrustedFileSystem.DeleteFile(BackupFile);
            return null;
        }
    }

    private static MaintenanceReadCandidate TryRead(string path)
    {
        if (!File.Exists(path))
        {
            return new MaintenanceReadCandidate(false, null);
        }
        try
        {
            var information = new FileInfo(path);
            if (information.Length <= 0 ||
                information.Length > MaximumStateBytes)
            {
                return new MaintenanceReadCandidate(true, null);
            }
            var state = JsonSerializer.Deserialize<InstallerMaintenanceState>(
                TrustedFileSystem.ReadAllText(path),
                JsonOptions);
            if (state is null)
            {
                return new MaintenanceReadCandidate(true, null);
            }
            Validate(state);
            return new MaintenanceReadCandidate(true, state);
        }
        catch (Exception error) when (
            error is JsonException or
                IOException or
                InvalidDataException or
                UnauthorizedAccessException)
        {
            return new MaintenanceReadCandidate(true, null);
        }
    }

    private static void Validate(InstallerMaintenanceState state)
    {
        if (state.FormatVersion is not (
                LegacyFormatVersion or CurrentFormatVersion) ||
            state.OwnerProcessId <= 0 ||
            state.OwnerStartedAtUtc == default ||
            state.BeganAtUtc == default ||
            state.OwnerStartedAtUtc > state.BeganAtUtc.AddMinutes(1) ||
            state.StagedVddAdoptionInstanceId is not null &&
                !ManagedVddOwnershipJournal.IsValidInstanceIdForAdoption(
                    state.StagedVddAdoptionInstanceId) ||
            state.Revision < 0 ||
            state.FormatVersion == LegacyFormatVersion &&
                (state.Revision != 0 ||
                 state.StagedVddAdoptionInstanceId is not null) ||
            state.FormatVersion == CurrentFormatVersion &&
                state.Revision == 0 &&
                state.StagedVddAdoptionInstanceId is not null)
        {
            throw new InvalidDataException(
                "The protected installer-maintenance fence contains an unsupported value. " +
                "Run setup again to recover it safely.");
        }
    }

    private static MaintenanceSafeguardSnapshot CaptureCurrentSnapshot()
    {
        var lifecycle = BackendLifecycleStateStore.Load();
        var backendWasEnabled =
            !lifecycle.DisabledIntentMarker &&
            lifecycle.State?.DesiredState != BackendDesiredState.Disabled;
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
        return new MaintenanceSafeguardSnapshot(
            backendWasEnabled,
            rescueAgent.State == ExactScheduledTaskState.Present,
            recovery.State == ExactScheduledTaskState.Present);
    }

    private static MaintenanceSafeguardSnapshot NormalizeSnapshotForTakeover(
        InstallerMaintenanceState existing)
    {
        if (existing.FormatVersion == CurrentFormatVersion)
        {
            return new MaintenanceSafeguardSnapshot(
                existing.BackendWasEnabled,
                existing.RescueAgentTaskWasPresent,
                existing.RecoveryTaskWasPresent);
        }

        // The first maintenance-fence format predated durable rollback
        // obligations. A stale v1 owner may have died after deleting either
        // task, so current absence is not evidence of the original baseline.
        // Conservatively require both safeguards for an Enabled lifecycle;
        // an exact Paused lifecycle requires neither.
        var lifecycle = BackendLifecycleStateStore.Load();
        var backendWasEnabled =
            !lifecycle.DisabledIntentMarker &&
            lifecycle.State?.DesiredState != BackendDesiredState.Disabled;
        return new MaintenanceSafeguardSnapshot(
            backendWasEnabled,
            backendWasEnabled,
            backendWasEnabled);
    }

    private static bool ResolveManagedVddBootstrapPermission(
        InstallerMaintenanceState? existing)
    {
        if (existing is not null)
        {
            return NormalizeSnapshotForTakeover(existing).BackendWasEnabled;
        }

        var preference = BackendLifecycleManager.ReadPreference();
        if (preference.State == BackendPreferenceState.Error)
        {
            throw new InvalidOperationException(
                "Installer maintenance cannot decide whether restarting the exact managed Vita VDD is permitted because the protected backend preference is unavailable. " +
                (preference.Error ?? "Unknown backend preference error."));
        }
        return ManagedVddBootstrapAllowedForTest(
            preference.State,
            existingBackendWasEnabled: null);
    }

    internal static bool ManagedVddBootstrapAllowedForTest(
        BackendPreferenceState preference,
        bool? existingBackendWasEnabled) =>
        existingBackendWasEnabled ??
        preference == BackendPreferenceState.Enabled;

    private static void VerifySafeToEnd(InstallerMaintenanceState state)
    {
        var lifecycle = BackendLifecycleStateStore.Load();
        var backendIsEnabled =
            !lifecycle.DisabledIntentMarker &&
            lifecycle.State?.DesiredState != BackendDesiredState.Disabled;
        var rescueAgent = HostRecoveryAgentManager.GetInstallationState();
        var recovery = RecoveryTaskManager.GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(
            rescueAgent,
            HostRecoveryAgentManager.TaskName);
        ExactScheduledTaskManager.RequireKnown(
            recovery,
            RecoveryTaskManager.TaskName);

        var normalized = state.FormatVersion == CurrentFormatVersion
            ? new MaintenanceSafeguardSnapshot(
                state.BackendWasEnabled,
                state.RescueAgentTaskWasPresent,
                state.RecoveryTaskWasPresent)
            : NormalizeSnapshotForTakeover(state);
        if (!CanEndForTest(
                backendIsEnabled,
                normalized.BackendWasEnabled,
                normalized.RescueAgentTaskWasPresent,
                normalized.RecoveryTaskWasPresent,
                rescueAgent.State,
                recovery.State))
        {
            throw new InvalidOperationException(
                backendIsEnabled
                    ? "Installer maintenance cannot end because one or more pre-existing Vita recovery safeguards have not been restored. Run setup again to finish recovery."
                    : "Installer maintenance cannot end because Vita host features are Paused but a Vita recovery task is still installed. Run setup again to finish the pause operation.");
        }

        // Task presence alone is not a safe handoff. Prove the display-side
        // invariant while holding the same cross-process lease used by every
        // stream and recovery mutation, then keep the durable maintenance
        // fence if any part is unfinished.
        using var displayTransaction = DisplayTransactionLock.Acquire();
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            throw new InvalidOperationException(
                "Installer maintenance cannot end while a display recovery transaction is pending. Setup can be retried without losing the saved physical layout.");
        }
        var suspend = DisplaySuspendIntentStore.Inspect();
        if (suspend.Disposition != DisplaySuspendIntentDisposition.Missing)
        {
            throw new InvalidOperationException(
                "Installer maintenance cannot end while Windows suspend/resume display recovery is active or incomplete.");
        }
        var topology = new DisplayTopologyService();
        if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                out var physical) ||
            physical is null)
        {
            throw new InvalidOperationException(
                "Installer maintenance cannot end because Windows did not expose a complete, available, physical-only display layout.");
        }
        var ownedDevices = File.Exists(
                ManagedVddOwnershipJournal.JournalFile)
            ? ManagedVddOwnershipJournal
                .RequireOwnedPresentDevicesLocked(
                    displayTransaction,
                    required: false)
            : [];
        if (ownedDevices.Any(device => device.Enabled))
        {
            throw new InvalidOperationException(
                "Installer maintenance cannot end because the exact Vita-owned virtual display device is still enabled while idle.");
        }
    }

    internal static bool CanEndForTest(
        bool backendIsEnabled,
        bool backendWasEnabled,
        bool rescueAgentTaskWasPresent,
        bool recoveryTaskWasPresent,
        ExactScheduledTaskState rescueAgentTask,
        ExactScheduledTaskState recoveryTask)
    {
        if (rescueAgentTask == ExactScheduledTaskState.Unknown ||
            recoveryTask == ExactScheduledTaskState.Unknown)
        {
            return false;
        }
        if (!backendIsEnabled)
        {
            return rescueAgentTask == ExactScheduledTaskState.Missing &&
                recoveryTask == ExactScheduledTaskState.Missing;
        }
        if (!backendWasEnabled)
        {
            // An explicit transition from Paused to Enabled is complete only
            // when both standard safeguards exist, regardless of the paused
            // snapshot's intentionally empty task baseline.
            return rescueAgentTask == ExactScheduledTaskState.Present &&
                recoveryTask == ExactScheduledTaskState.Present;
        }
        return (!rescueAgentTaskWasPresent ||
                rescueAgentTask == ExactScheduledTaskState.Present) &&
            (!recoveryTaskWasPresent ||
                recoveryTask == ExactScheduledTaskState.Present);
    }

    internal static bool MustBootstrapBeforeProtectedStateForTest(
        bool protectionInitialized) =>
        MustBootstrapBeforeProtectedState(protectionInitialized);

    private static bool MustBootstrapBeforeProtectedState(
        bool protectionInitialized) =>
        !protectionInitialized;

    private static bool CanReuseLiveOwner(
        InstallerMaintenanceState? existing,
        int ownerProcessId,
        DateTimeOffset ownerStart)
    {
        if (existing is null || !IsRecordedOwnerAlive(existing)) return false;
        if (existing.OwnerProcessId == ownerProcessId &&
            existing.OwnerStartedAtUtc.UtcTicks == ownerStart.UtcTicks)
        {
            return true;
        }

        // Refuse the competing installer before recovery stops a host or
        // touches topology owned by the verified live setup. The bootstrap-
        // first legacy branch can reach this only for unsupported manually
        // injected state and still fails closed after physical recovery.
        throw new InvalidOperationException(
            $"Vita Moonlight setup or repair is already running under process {existing.OwnerProcessId}. " +
            "Finish or close that installer before starting another maintenance operation.");
    }

    private static FileStream AcquireCommandGate()
    {
        MachineStateSecurity.SecureContainer();
        try
        {
            return TrustedFileSystem.OpenExclusiveFile(CommandGateFile);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                "Another Vita Moonlight host change is already running. Wait for it to finish, then try again.",
                error);
        }
    }

    private static int? GetOwnerProcessId(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index].StartsWith(
                    OwnerOption + "=",
                    StringComparison.OrdinalIgnoreCase))
            {
                return ParseOwnerProcessId(
                    args[index][(OwnerOption.Length + 1)..]);
            }
            if (!args[index].Equals(
                    OwnerOption,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    $"{OwnerOption} requires a process id.");
            }
            return ParseOwnerProcessId(args[index + 1]);
        }
        return null;
    }

    private static bool IsDeadOwnerUninstallBridge(
        string command,
        IReadOnlyList<string> args) =>
        command.Equals("uninstall", StringComparison.OrdinalIgnoreCase) &&
        args.FirstOrDefault()?.Equals(
            "prepare",
            StringComparison.OrdinalIgnoreCase) == true &&
        args.Any(argument => argument.Equals(
            "--begin",
            StringComparison.OrdinalIgnoreCase));

    private static bool IsPhysicalRecoveryCommand(
        string command,
        IReadOnlyList<string> args) =>
        command.Equals("session", StringComparison.OrdinalIgnoreCase) &&
        args.FirstOrDefault() is { } action &&
        (action.Equals("recover", StringComparison.OrdinalIgnoreCase) ||
         action.Equals("stop", StringComparison.OrdinalIgnoreCase));

    private static int ParseOwnerProcessId(string value) =>
        int.TryParse(value, out var processId) && processId > 0
            ? processId
            : throw new ArgumentException(
                $"{OwnerOption} requires a positive process id.");

    private static DateTimeOffset RequireLiveProcessStart(int processId)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(processId),
                "The installer owner process id must be positive.");
        }
        try
        {
            using var process = Process.GetProcessById(processId);
            var started = process.StartTime.ToUniversalTime();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Installer owner process {processId} has already ended.");
            }
            return new DateTimeOffset(started, TimeSpan.Zero);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException(
                $"Installer owner process {processId} is not running.",
                error);
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException(
                $"Windows could not verify installer owner process {processId}. " +
                "The maintenance fence was not changed.",
                error);
        }
    }

    private static bool IsRecordedOwnerAlive(
        InstallerMaintenanceState state)
    {
        try
        {
            using var process = Process.GetProcessById(
                state.OwnerProcessId);
            var started = process.StartTime.ToUniversalTime();
            return !process.HasExited &&
                state.OwnerStartedAtUtc.UtcTicks == started.Ticks;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception error)
        {
            // Unknown is not dead. Refuse takeover rather than allowing two
            // installers to own the same protected transaction.
            throw new InvalidOperationException(
                $"Windows could not verify whether installer owner process {state.OwnerProcessId} is still running. " +
                "The maintenance fence was not changed.",
                error);
        }
    }

    internal static void RequireBootstrapHelper(int ownerProcessId)
    {
        RequireLiveProcessStart(ownerProcessId);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException(
                "The installer maintenance helper path is unavailable.");
        }
        var actual = Path.GetFullPath(executable);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(temporaryRoot, actual);
        if (!Path.GetFileName(actual).Equals(
                "VitaMoonlight.Host.Maintenance.exe",
                StringComparison.OrdinalIgnoreCase) ||
            relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            (File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Legacy state migration requires the maintenance helper extracted by the Vita Moonlight installer.");
        }
    }

    internal static void RequireControllerProcess()
    {
        if (InstallationTrust.IsInstalledPayload(out _)) return;
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable) ||
            !Path.GetFileName(executable).Equals(
                "VitaMoonlight.Host.Maintenance.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Installer maintenance can be controlled only by the installed host or the helper extracted by setup.");
        }
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var actual = Path.GetFullPath(executable);
        var relative = Path.GetRelativePath(temporaryRoot, actual);
        if (relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            (File.GetAttributes(actual) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "The installer maintenance helper is outside the trusted setup temporary directory.");
        }
    }

    private sealed class CommandAccessLease : IDisposable
    {
        private FileStream? commandGate;
        private readonly bool ownsFence;

        internal CommandAccessLease(
            FileStream commandGate,
            bool ownsFence)
        {
            this.commandGate = commandGate;
            this.ownsFence = ownsFence;
            if (ownsFence)
            {
                OwnedCommandDepth.Value++;
            }
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(
                ref commandGate,
                null);
            if (gate is null) return;
            if (ownsFence)
            {
                OwnedCommandDepth.Value--;
            }
            gate.Dispose();
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static EmptyLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record MaintenanceReadCandidate(
        bool Exists,
        InstallerMaintenanceState? State);

    private sealed record MaintenanceSafeguardSnapshot(
        bool BackendWasEnabled,
        bool RescueAgentTaskWasPresent,
        bool RecoveryTaskWasPresent);
}
