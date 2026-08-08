namespace VitaMoonlight.Host;

internal sealed record BackendLifecycleComponents(
    bool RecoveryTaskInstalled,
    bool RescueAgentTaskInstalled,
    bool RescueAgentRunning,
    bool RecoveryPending,
    int ActivePhysicalDisplayCount,
    bool ManagedVirtualDisplayInstalled,
    bool ManagedVirtualDisplayEnabled,
    bool ManagedVirtualDisplayActive,
    IReadOnlyList<ManagedVddDeviceStatus> ManagedVirtualDisplayDevices,
    SunshineBackendObservation Sunshine);

internal sealed record BackendLifecycleReport(
    BackendLifecycleStatus Status,
    BackendDesiredState DesiredState,
    bool PreferencePersisted,
    DateTimeOffset? UpdatedAtUtc,
    BackendLifecycleComponents? Components,
    IReadOnlyList<string> Issues,
    string? StorageWarning);

internal sealed record BackendLifecycleEvaluation(
    BackendLifecycleStatus Status,
    IReadOnlyList<string> Issues);

internal enum BackendPreferenceState
{
    Enabled,
    Disabled,
    Error,
}

internal sealed record BackendPreferenceResult(
    BackendPreferenceState State,
    bool Persisted,
    string? Error);

internal sealed record BackendUninstallHandoff(
    BackendLifecycleReport Report,
    BackendPersistedState? DisabledRollbackState);

internal static class BackendLifecycleManager
{
    /// <summary>
    /// Fast, fail-safe intent check for the background recovery agent and
    /// session hooks. Existing installations without lifecycle state remain
    /// enabled. An explicit Disabled intent takes effect before any device or
    /// device/task mutation. Unreadable state remains paused rather than
    /// guessing saved Vita-owned component state.
    /// </summary>
    internal static bool IsEnabled
    {
        get
        {
            try
            {
                if (BackendLifecycleStateStore.IsUninstallInProgress())
                {
                    return false;
                }
                if (InstallerMaintenanceFence.IsPresent &&
                    !InstallerMaintenanceFence.CurrentCommandOwnsFence)
                {
                    return false;
                }
                if (File.Exists(DeferredHostSetupStore.PlanFile))
                {
                    // The lifecycle may have restored its components, but no
                    // stream may begin until the protected deferred setup has
                    // committed and removed this plan.
                    return false;
                }
                var loaded = BackendLifecycleStateStore.Load();
                return !loaded.DisabledIntentMarker &&
                    loaded.State?.DesiredState !=
                    BackendDesiredState.Disabled;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static BackendPreferenceResult ReadPreference()
    {
        try
        {
            if (BackendLifecycleStateStore.IsUninstallInProgress())
            {
                return new BackendPreferenceResult(
                    BackendPreferenceState.Error,
                    true,
                    "Vita Moonlight Host uninstall is in progress. Finish or retry uninstall before changing host features.");
            }
            if (InstallerMaintenanceFence.IsPresent &&
                !InstallerMaintenanceFence.CurrentCommandOwnsFence)
            {
                return new BackendPreferenceResult(
                    BackendPreferenceState.Error,
                    true,
                    "Vita Moonlight setup or repair is in progress. Finish it, or rerun the installer after an interruption, before changing host features.");
            }
            var loaded = BackendLifecycleStateStore.Load();
            var disabled = loaded.DisabledIntentMarker ||
                loaded.State?.DesiredState == BackendDesiredState.Disabled;
            if (!disabled && File.Exists(DeferredHostSetupStore.PlanFile))
            {
                return new BackendPreferenceResult(
                    BackendPreferenceState.Error,
                    true,
                    "Selected host setup is still being completed. Wait for Enable to finish or retry it before streaming.");
            }
            return new BackendPreferenceResult(
                disabled
                    ? BackendPreferenceState.Disabled
                    : BackendPreferenceState.Enabled,
                loaded.Exists,
                null);
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            return new BackendPreferenceResult(
                BackendPreferenceState.Error,
                true,
                error.Message);
        }
    }

    internal static BackendLifecycleReport Inspect()
    {
        var installerMaintenance =
            InstallerMaintenanceFence.IsPresent &&
            !InstallerMaintenanceFence.CurrentCommandOwnsFence;
        try
        {
            if (BackendLifecycleStateStore.IsUninstallInProgress())
            {
                return ErrorReport(
                    "Vita Moonlight Host uninstall is in progress. Finish or retry uninstall before starting a Vita session.",
                    BackendDesiredState.Disabled,
                    preferencePersisted: true);
            }
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            return ErrorReport(
                error.Message,
                BackendDesiredState.Disabled,
                preferencePersisted: true);
        }

        BackendStateLoadResult loaded;
        try
        {
            loaded = BackendLifecycleStateStore.Load();
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            return ErrorReport(
                error.Message,
                BackendDesiredState.Disabled,
                preferencePersisted: true);
        }

        try
        {
            var state = loaded.State ?? CaptureCurrentAsEnabled();
            if (loaded.DisabledIntentMarker)
            {
                state = state with
                {
                    DesiredState = BackendDesiredState.Disabled,
                };
            }
            var report = InspectCore(
                state,
                loaded.Exists,
                loaded.StorageWarning);
            var deferredSetup = DeferredHostSetupStore.Load();
            if (state.DesiredState == BackendDesiredState.Enabled &&
                deferredSetup is not null)
            {
                return report with
                {
                    Status = BackendLifecycleStatus.Partial,
                    Issues = report.Issues
                        .Append(
                            "Selected host setup has not completed; streaming remains blocked until Enable finishes it.")
                        .Distinct()
                        .ToArray(),
                };
            }
            if (installerMaintenance)
            {
                return report with
                {
                    Status = report.Status == BackendLifecycleStatus.Error
                        ? BackendLifecycleStatus.Error
                        : BackendLifecycleStatus.Partial,
                    Issues = report.Issues
                        .Append(
                            "Setup or repair is in progress; new Vita sessions and host changes remain blocked until its live owner finishes.")
                        .Distinct()
                        .ToArray(),
                };
            }
            return report;
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            return ErrorReport(
                error.Message,
                loaded.State?.DesiredState ?? BackendDesiredState.Enabled,
                loaded.Exists);
        }
    }

    internal static BackendLifecycleReport Disable()
    {
        using var operation = BackendLifecycleStateStore.AcquireLock();
        return DisableLocked(
            operation,
            allowUninstallRollback: false);
    }

    internal static BackendLifecycleReport DisableLocked(
        BackendOperationLease operation,
        bool allowUninstallRollback)
    {
        operation.RequireActive();
        if (!allowUninstallRollback)
        {
            BackendLifecycleStateStore.RequireNoUninstallInProgress();
        }
        var loaded = BackendLifecycleStateStore.LoadForLifecycleAction(
            operation);
        var state = loaded.State;
        if (state is null ||
            !loaded.DisabledIntentMarker &&
            state.DesiredState != BackendDesiredState.Disabled)
        {
            state = CaptureCurrentAsEnabled();
            state = state with
            {
                Revision = loaded.State is null
                    ? state.Revision
                    : checked(loaded.State.Revision + 1),
                DesiredState = BackendDesiredState.Disabled,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastKnownStatus = BackendLifecycleStatus.Partial,
                LastError = null,
            };
        }
        else
        {
            state = BackendLifecycleStateStore.WithNextRevision(
                state,
                BackendDesiredState.Disabled,
                BackendLifecycleStatus.Partial);
        }

        // Persist user intent and the exact pre-disable restore snapshot
        // before touching Windows. A repeated disable preserves the original
        // snapshot rather than learning already-disabled component state.
        BackendLifecycleStateStore.Save(state);

        try
        {
            RestoreAndVerifyPhysicalOnly();
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            var failed = BackendLifecycleStateStore.WithNextRevision(
                state,
                BackendDesiredState.Disabled,
                BackendLifecycleStatus.Error,
                "Physical-display safety verification failed before backend components were changed: " +
                error.Message);
            BackendLifecycleStateStore.Save(failed);
            throw new InvalidOperationException(
                "Vita Moonlight is marked disabled, but no task or device was disabled because Windows could not prove a safe physical-only display layout. " +
                error.Message,
                error);
        }

        // Keep both safeguards alive until the higher-risk PnP mutation has
        // completed and Windows has again proved a physical-only topology.
        try
        {
            SetManagedVirtualDisplaysWithPhysicalSafety(
                state.ManagedVirtualDisplayInstancesToRestore,
                enabled: false);
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            var failed = BackendLifecycleStateStore.WithNextRevision(
                state,
                BackendDesiredState.Disabled,
                BackendLifecycleStatus.Partial,
                "The managed Vita display could not be disabled and reverified; recovery safeguards were kept. " +
                error.Message);
            BackendLifecycleStateStore.Save(failed);
            throw new InvalidOperationException(
                "Vita Moonlight is marked paused, but Windows could not safely finish disabling the managed Vita display. " +
                "Both recovery safeguards were kept; choose Pause Vita host features again. " +
                error.Message,
                error);
        }

        var stepErrors = new List<string>();
        TryStep(
            "make a final bounded attempt to restore the exact pre-stream Windows audio defaults",
            TryPendingAudioRecoveryBeforePause,
            stepErrors);
        if (stepErrors.Count == 0)
        {
            // Do not remove the only asynchronous retry mechanism while an
            // HDMI/DisplayPort endpoint is still re-enumerating. A failed
            // Pause remains visibly partial with both safeguards intact.
            TryStep(
                "stop and remove the stream rescue agent task",
                HostRecoveryAgentManager.Uninstall,
                stepErrors);
            TryStep(
                "remove the automatic logon recovery task",
                RecoveryTaskManager.Uninstall,
                stepErrors);
        }
        TryStep(
            "perform the final physical-display safety verification",
            RestoreAndVerifyPhysicalOnly,
            stepErrors);

        if (stepErrors.Count > 0)
        {
            // A partial task removal must not discard every automatic safety
            // path. Restore only safeguards that existed before Pause.
            if (state.RestoreRecoveryTask)
            {
                TryStep(
                    "restore the automatic logon recovery task after an incomplete pause",
                    () => RecoveryTaskManager.Install(ExecutablePath()),
                    stepErrors);
            }
            if (state.RestoreRescueAgent)
            {
                TryStep(
                    "restore the stream rescue agent after an incomplete pause",
                    () => HostRecoveryAgentManager.Install(ExecutablePath()),
                    stepErrors);
            }
        }

        var preliminary = InspectCore(
            state with
            {
                LastKnownStatus = BackendLifecycleStatus.Disabled,
                LastError = null,
            },
            true,
            null);
        var finalStatus = stepErrors.Count == 0
            ? preliminary.Status
            : BackendLifecycleStatus.Partial;
        var allIssues = preliminary.Issues.Concat(stepErrors).Distinct().ToArray();
        var saved = BackendLifecycleStateStore.WithNextRevision(
            state,
            BackendDesiredState.Disabled,
            finalStatus,
            allIssues.Length == 0 ? null : string.Join(" ", allIssues));
        BackendLifecycleStateStore.Save(saved);
        return InspectCore(saved, true, null);
    }

    private static void TryPendingAudioRecoveryBeforePause()
    {
        try
        {
            var audio = AudioEndpointRecoveryService.RestorePending(
                waitForEndpoint: true);
            if (audio.Succeeded) return;
            Console.Error.WriteLine(
                "Windows has not re-enumerated every exact pre-stream audio endpoint. " +
                "Pause will still stop all background functions; the inert exact endpoint record is retained and will be retried if Vita host features are enabled again. " +
                (audio.Detail ?? string.Empty));
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            Console.Error.WriteLine(
                "Pause could not inspect the supplementary audio recovery record, but will still stop all background functions. " +
                error.Message);
        }
    }

    internal static BackendLifecycleReport Enable()
    {
        using var operation = BackendLifecycleStateStore.AcquireLock();
        return EnableLocked(operation);
    }

    internal static BackendLifecycleReport EnableLocked(
        BackendOperationLease operation)
    {
        operation.RequireActive();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        ScheduledTaskAccount.RequireCurrentInteractiveUser(
            "Enabling Vita host features");
        var loaded = BackendLifecycleStateStore.LoadForLifecycleAction(
            operation);
        var state = loaded.State ?? CaptureCurrentAsEnabled();
        var deferredSetup = DeferredHostSetupStore.Load();
        if (deferredSetup?.InstallVirtualDisplay == true &&
            state.ManagedVirtualDisplayInstancesToRestore.Count > 0)
        {
            // An explicit installer repair may be needed precisely because a
            // previously recorded device instance disappeared. Preserve only
            // exact instances which still exist; the deferred, protected
            // driver install will create/repair the device after the user has
            // explicitly enabled host features. Never substitute a different
            // existing shared instance here.
            var presentInstanceIds = DisplayWizardAdapter
                .InspectManagedDriverDevices()
                .Where(device => device.Present)
                .Select(device => device.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            state = state with
            {
                ManagedVirtualDisplayInstancesToRestore = state
                    .ManagedVirtualDisplayInstancesToRestore
                    .Where(presentInstanceIds.Contains)
                    .ToArray(),
            };
        }
        if (!loaded.DisabledIntentMarker &&
            state.DesiredState == BackendDesiredState.Enabled)
        {
            var current = InspectCore(state, loaded.Exists, loaded.StorageWarning);
            if (current.Status == BackendLifecycleStatus.Enabled)
            {
                return current;
            }
        }

        // Keep the durable Disabled marker until every component and the
        // physical topology have been restored and verified. A task started
        // during this transition runs in physical-safety-only mode.
        var transition = BackendLifecycleStateStore.WithNextRevision(
            state,
            BackendDesiredState.Disabled,
            BackendLifecycleStatus.Partial,
            "Enable is in progress; streaming remains blocked until verification completes.");
        BackendLifecycleStateStore.Save(transition);

        var stepErrors = new List<string>();
        if (state.RestoreRecoveryTask)
        {
            TryStep(
                "restore the automatic logon recovery task",
                () => RecoveryTaskManager.Install(ExecutablePath()),
                stepErrors);
        }
        if (state.RestoreRescueAgent)
        {
            TryStep(
                "restore and start the stream rescue agent task",
                () => HostRecoveryAgentManager.Install(ExecutablePath()),
                stepErrors);
        }
        // Enabled host features are idle-ready, not display-armed. The exact
        // managed device remains disabled until a journaled Vita stream start
        // owns the display transaction.
        TryStep(
            "enter the safe idle display state",
            RestoreAndVerifyPhysicalOnly,
            stepErrors);

        var enabledCandidate = BackendLifecycleStateStore.WithNextRevision(
            transition,
            BackendDesiredState.Enabled,
            BackendLifecycleStatus.Enabled);
        var preliminary = InspectCore(enabledCandidate, true, null);
        var allIssues = preliminary.Issues
            .Concat(stepErrors)
            .Distinct()
            .ToArray();
        if (allIssues.Length == 0 &&
            preliminary.Status == BackendLifecycleStatus.Enabled)
        {
            try
            {
                BackendLifecycleStateStore.Save(enabledCandidate);
                return InspectCore(enabledCandidate, true, null);
            }
            catch (Exception error) when (IsOperationalError(error))
            {
                allIssues =
                [.. allIssues, "Could not commit Enabled intent: " + error.Message];
            }
        }

        var rollbackErrors = new List<string>();
        TryStep(
            "recover the physical display after the incomplete enable",
            RestoreAndVerifyPhysicalOnly,
            rollbackErrors);
        TryStep(
            "return the managed virtual display to its idle state",
            RestoreAndVerifyPhysicalOnly,
            rollbackErrors);
        TryStep(
            "reverify the physical display after enable rollback",
            RestoreAndVerifyPhysicalOnly,
            rollbackErrors);
        TryStep(
            "remove the stream rescue task restored by the incomplete enable",
            HostRecoveryAgentManager.Uninstall,
            rollbackErrors);
        TryStep(
            "remove the logon recovery task restored by the incomplete enable",
            RecoveryTaskManager.Uninstall,
            rollbackErrors);

        var failureIssues = allIssues
            .Concat(rollbackErrors)
            .DefaultIfEmpty("Enabled state could not be verified.")
            .Distinct()
            .ToArray();
        var failed = BackendLifecycleStateStore.WithNextRevision(
            transition,
            BackendDesiredState.Disabled,
            rollbackErrors.Count == 0
                ? BackendLifecycleStatus.Partial
                : BackendLifecycleStatus.Error,
            string.Join(" ", failureIssues));
        BackendLifecycleStateStore.Save(failed);
        var failedReport = InspectCore(failed, true, null);
        return failedReport with
        {
            Status = failed.LastKnownStatus,
            Issues = failureIssues,
        };
    }

    /// <summary>
    /// Restores shared component state whose original values would otherwise
    /// be lost when the product state directory is removed. It deliberately
    /// does not recreate Vita-owned scheduled tasks during uninstall.
    /// </summary>
    internal static BackendUninstallHandoff PrepareForUninstall(
        DisplayTransactionLease transaction,
        bool restoreSunshine,
        bool restoreManagedVdd)
    {
        transaction.RequireActive();
        // Sunshine is a shared dependency and is never part of the Vita-owned
        // backend lifecycle. Keep the parameter for installer compatibility.
        _ = restoreSunshine;
        // Retaining the shared driver package must never retain an available
        // 960x544 fallback monitor after Vita safeguards are removed.
        _ = restoreManagedVdd;
        var hasManagedVddOwnership = File.Exists(
            ManagedVddOwnershipJournal.JournalFile);
        if (hasManagedVddOwnership)
        {
            RestoreAndVerifyPhysicalOnlyLocked(transaction);
        }
        else
        {
            // An incomplete/older installation can have a foreign or
            // unproven MTT node but no exact Vita ownership. Finalization may
            // proceed only when Windows already proves a complete
            // physical-only layout; it must not route that node through the
            // generic idle reconciler, which correctly refuses unowned PnP
            // mutation.
            var topology = new DisplayTopologyService();
            if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                    out var physicalOnly) ||
                physicalOnly is null)
            {
                throw new InvalidOperationException(
                    "Uninstall cannot finalize an installation with no exact virtual-display ownership unless Windows already exposes a complete physical-only display layout. No unowned display device was changed.");
            }
        }
        BackendStateLoadResult loaded;
        try
        {
            loaded = BackendLifecycleStateStore.Load();
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            // Uninstall must remain possible when a durable Disabled marker
            // outlives corrupt snapshots. The physical-only, VDD-disabled
            // invariant above is authoritative even when preferences are not.
            return new BackendUninstallHandoff(
                ErrorReport(
                    "The Vita host-feature state is unreadable. Uninstall kept Sunshine unchanged and preserved the already verified physical-only desktop without guessing virtual-display ownership. " +
                    error.Message,
                    BackendDesiredState.Disabled,
                    preferencePersisted: true),
                null);
        }
        if (!hasManagedVddOwnership)
        {
            // No journal means there is no display-side rollback authority to
            // preserve. Return only an informational lifecycle view; task
            // rollback is tracked independently by the finalizer, and neither
            // success nor failure may route an old saved instance ID through
            // SetManagedDriverEnabled.
            if (loaded.State is null)
            {
                return new BackendUninstallHandoff(
                    ErrorReport(
                        "No protected Vita virtual-display ownership exists. Uninstall will remove only Vita-owned tasks, integration, and files while leaving every unproven MTT device and shared package unchanged.",
                        BackendDesiredState.Disabled,
                        preferencePersisted: false),
                    null);
            }
            var displaySanitized = loaded.State with
            {
                ManagedVirtualDisplayInstancesToRestore = [],
            };
            return new BackendUninstallHandoff(
                InspectCore(
                    displaySanitized,
                    true,
                    loaded.StorageWarning),
                null);
        }
        if (loaded.State is null)
        {
            return new BackendUninstallHandoff(
                InspectCore(
                    CaptureCurrentAsEnabled(),
                    false,
                    loaded.StorageWarning),
                null);
        }

        var state = loaded.State;
        if (state.DesiredState != BackendDesiredState.Disabled)
        {
            return new BackendUninstallHandoff(
                InspectCore(state, true, loaded.StorageWarning),
                null);
        }
        try
        {
            RestoreAndVerifyPhysicalOnlyLocked(transaction);
            var restored = BackendLifecycleStateStore.WithNextRevision(
                state,
                BackendDesiredState.Disabled,
                BackendLifecycleStatus.Partial,
                "Uninstall prepared a physical-only desktop and disabled the Vita virtual display. " +
                "If uninstall is cancelled or fails, run Pause Vita host features again.");
            BackendLifecycleStateStore.SaveLocked(transaction, restored);
            return new BackendUninstallHandoff(
                InspectCore(restored, true, null),
                state);
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            var rollbackErrors = new List<string>();
            TryStep(
                "recover the physical display after uninstall handoff failed",
                () => RestoreAndVerifyPhysicalOnlyLocked(transaction),
                rollbackErrors);
            TryStep(
                "return the exact managed virtual displays to their paused state",
                () => DisplayWizardAdapter.SetManagedDriverEnabled(
                    transaction,
                    state.ManagedVirtualDisplayInstancesToRestore,
                    enabled: false),
                rollbackErrors);
            TryStep(
                "reverify the physical display after uninstall handoff rollback",
                () => RestoreAndVerifyPhysicalOnlyLocked(transaction),
                rollbackErrors);
            TryStep(
                "re-save the protected Disabled lifecycle snapshot",
                () => BackendLifecycleStateStore.SaveLocked(
                    transaction,
                    BackendLifecycleStateStore.WithNextRevision(
                        state,
                        BackendDesiredState.Disabled,
                        rollbackErrors.Count == 0
                            ? BackendLifecycleStatus.Disabled
                            : BackendLifecycleStatus.Error,
                        rollbackErrors.Count == 0
                            ? null
                            : string.Join(" ", rollbackErrors))),
                rollbackErrors);

            if (rollbackErrors.Count == 0)
            {
                throw new InvalidOperationException(
                "Uninstall could not verify the idle managed Vita display state. " +
                    "The original paused state and physical desktop were restored.",
                    error);
            }
            throw new AggregateException(
                "Uninstall handoff failed and its paused-state rollback also needs attention.",
                new[] { error }.Concat(
                    rollbackErrors.Select(message =>
                        new InvalidOperationException(message))));
        }
    }

    /// <summary>
    /// Rolls a failed uninstall handoff back to the still-persisted Disabled
    /// intent. Removed optional shared components are treated as already
    /// paused, making this safe after a partial dependency removal.
    /// </summary>
    internal static BackendLifecycleReport
        RestoreDisabledStateAfterFailedUninstall(
            BackendPersistedState disabledRollbackState)
    {
        if (disabledRollbackState.DesiredState !=
            BackendDesiredState.Disabled)
        {
            throw new InvalidDataException(
                "The uninstall rollback snapshot does not contain an intentional Disabled state.");
        }

        using var operation = BackendLifecycleStateStore.AcquireLock();
        BackendPersistedState? currentState = null;
        try
        {
            currentState = BackendLifecycleStateStore.Load().State;
        }
        catch (InvalidDataException)
        {
            // The in-memory handoff token is authoritative precisely for
            // this case: final cleanup may already have removed either
            // or both on-disk copies before a later uninstall step fails.
        }
        var nextRevision = checked(Math.Max(
            currentState?.Revision ?? 0,
            disabledRollbackState.Revision) + 1);
        var restored = disabledRollbackState with
        {
            Revision = nextRevision,
            DesiredState = BackendDesiredState.Disabled,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LastKnownStatus = BackendLifecycleStatus.Partial,
            LastError =
                "Uninstall did not complete; the saved paused Vita host-feature state was restored.",
        };
        BackendLifecycleStateStore.Save(restored);
        // The uninstall transaction marker intentionally remains present
        // until rollback is complete. This private path is the sole backend
        // mutation permitted while that marker blocks user-driven
        // Enable/Disable operations.
        return DisableLocked(
            operation,
            allowUninstallRollback: true);
    }

    internal static BackendLifecycleReport
        RestoreDisabledStateAfterFailedUninstall()
    {
        var loaded = BackendLifecycleStateStore.Load();
        if (loaded.State?.DesiredState != BackendDesiredState.Disabled)
        {
            throw new InvalidOperationException(
                "The exact pre-uninstall Disabled snapshot is unavailable. " +
                "Uninstall rollback will not guess the prior Vita device and safeguard state.");
        }
        return RestoreDisabledStateAfterFailedUninstall(loaded.State);
    }

    internal static BackendLifecycleEvaluation Evaluate(
        BackendPersistedState state,
        BackendLifecycleComponents components,
        IEnumerable<string>? observationErrors = null,
        string? storageWarning = null)
    {
        var errors = observationErrors?
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct()
            .ToArray() ?? [];
        if (errors.Length > 0)
        {
            return new BackendLifecycleEvaluation(
                BackendLifecycleStatus.Error,
                errors);
        }

        var issues = new List<string>();
        if (!string.IsNullOrWhiteSpace(storageWarning))
        {
            issues.Add(storageWarning);
        }
        if (state.DesiredState == BackendDesiredState.Disabled)
        {
            if (components.ActivePhysicalDisplayCount <= 0)
            {
                issues.Add("No active physical display was detected.");
            }
            if (components.RecoveryPending)
            {
                issues.Add("A display recovery transaction is still pending.");
            }
            if (components.RecoveryTaskInstalled)
            {
                issues.Add("The automatic logon recovery task is still installed.");
            }
            if (components.RescueAgentTaskInstalled ||
                components.RescueAgentRunning)
            {
                issues.Add("The stream rescue agent is still installed or running.");
            }
            if (components.ManagedVirtualDisplayActive)
            {
                issues.Add("The managed virtual display is still active in the desktop topology.");
            }
            if (state.ManagedVirtualDisplayInstancesToRestore.Any(instanceId =>
                    components.ManagedVirtualDisplayDevices.Any(device =>
                        device.Present &&
                        device.Enabled &&
                        string.Equals(
                            device.InstanceId,
                            instanceId,
                            StringComparison.OrdinalIgnoreCase))))
            {
                issues.Add("A managed virtual display instance that Vita Moonlight paused is still enabled.");
            }
        }
        else
        {
            if (state.RestoreRecoveryTask &&
                !components.RecoveryTaskInstalled)
            {
                issues.Add("The automatic logon recovery task has not been restored.");
            }
            if (state.RestoreRescueAgent &&
                (!components.RescueAgentTaskInstalled ||
                 !components.RescueAgentRunning))
            {
                issues.Add("The stream rescue agent has not been restored and started.");
            }
            if (state.ManagedVirtualDisplayInstancesToRestore.Any(instanceId =>
                    !components.ManagedVirtualDisplayDevices.Any(device =>
                        device.Present &&
                        string.Equals(
                            device.InstanceId,
                            instanceId,
                            StringComparison.OrdinalIgnoreCase))))
            {
                issues.Add("A recorded managed virtual display instance is no longer present.");
            }
            if (components.RecoveryPending)
            {
                if (!components.ManagedVirtualDisplayEnabled)
                {
                    issues.Add(
                        "A Vita stream transaction is pending, but its managed virtual display device is not enabled.");
                }
            }
            else
            {
                if (components.ActivePhysicalDisplayCount <= 0)
                {
                    issues.Add(
                        "No active physical display was detected while the Vita host is idle.");
                }
                if (components.ManagedVirtualDisplayActive)
                {
                    issues.Add(
                        "The managed virtual display is active without a Vita stream transaction.");
                }
                if (components.ManagedVirtualDisplayEnabled)
                {
                    issues.Add(
                        "The managed virtual display device is still enabled while the Vita host is idle.");
                }
            }
        }

        if (!SunshineBackendController.MatchesDesiredState(
                state.DesiredState,
                state.Sunshine,
                components.Sunshine))
        {
            issues.Add(
                "A legacy Sunshine lifecycle record is invalid. Shared Sunshine was left unchanged.");
        }

        return new BackendLifecycleEvaluation(
            issues.Count == 0
                ? state.DesiredState == BackendDesiredState.Enabled
                    ? BackendLifecycleStatus.Enabled
                    : BackendLifecycleStatus.Disabled
                : BackendLifecycleStatus.Partial,
            issues);
    }

    private static BackendPersistedState CaptureCurrentAsEnabled()
    {
        // Pause authority comes from the exact protected VDD journal, never
        // from a hardware-ID-wide enumeration. A missing journal with a
        // present MTT node is an unowned/ambiguous configuration and fails
        // before the lifecycle snapshot can authorize a later toggle.
        var devices = ManagedVddOwnershipJournal
            .RequireOwnedPresentDevices(required: false);
        var recoveryTask = RecoveryTaskManager.GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(
            recoveryTask,
            RecoveryTaskManager.TaskName);
        var rescueTask = HostRecoveryAgentManager.GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(
            rescueTask,
            HostRecoveryAgentManager.TaskName);
        var rescueAgentRunning =
            HostRecoveryAgentManager.IsRunning();
        return BackendLifecycleStateStore.Create(
            BackendDesiredState.Enabled,
            recoveryTask.State == ExactScheduledTaskState.Present,
            rescueTask.State == ExactScheduledTaskState.Present ||
                rescueAgentRunning,
            devices
                .Where(device => device.Present)
                .Select(device => device.InstanceId)
                .ToArray(),
            SunshineBackendController.CaptureManagedState());
    }

    private static BackendLifecycleReport InspectCore(
        BackendPersistedState state,
        bool preferencePersisted,
        string? storageWarning)
    {
        var errors = new List<string>();
        var recoveryTask = RecoveryTaskManager.GetInstallationState();
        if (recoveryTask.State == ExactScheduledTaskState.Unknown)
        {
            errors.Add(
                "Could not inspect automatic recovery task: " +
                (recoveryTask.Error ?? "unknown Task Scheduler error"));
        }
        var recoveryTaskInstalled =
            recoveryTask.State == ExactScheduledTaskState.Present;
        var rescueTask = HostRecoveryAgentManager.GetInstallationState();
        if (rescueTask.State == ExactScheduledTaskState.Unknown)
        {
            errors.Add(
                "Could not inspect stream rescue task: " +
                (rescueTask.Error ?? "unknown Task Scheduler error"));
        }
        var rescueTaskInstalled =
            rescueTask.State == ExactScheduledTaskState.Present;
        var rescueAgentRunning = Observe(
            "stream rescue agent",
            HostRecoveryAgentManager.IsRunning,
            false,
            errors);
        var recoveryPending = File.Exists(HostStatePaths.RecoveryFile);

        var devices = Observe(
            "managed virtual display device",
            DisplayWizardAdapter.InspectManagedDriverDevices,
            Array.Empty<ManagedVddDeviceStatus>(),
            errors);
        var vddInstalled = devices.Count > 0 ||
            DisplayWizardAdapter.IsDriverInstalled();
        var vddEnabled = devices.Any(device =>
            device.Present && device.Enabled);

        var displayActive = false;
        var activePhysicalDisplayCount = 0;
        try
        {
            var displays = new DisplayTopologyService().ListDisplays();
            displayActive = displays.Any(display =>
                display.IsActive &&
                DisplayTopologyService.IsManagedVirtualDisplay(display));
            activePhysicalDisplayCount = displays.Count(display =>
                display.IsActive &&
                display.IsAvailable &&
                !DisplayTopologyService.IsLikelyVirtualDisplay(display));
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            errors.Add("Could not inspect the active display topology: " +
                error.Message);
        }

        var sunshine = SunshineBackendController.Inspect(state.Sunshine);
        if (sunshine.Error is not null)
        {
            errors.Add("Could not inspect Sunshine: " + sunshine.Error);
        }
        var components = new BackendLifecycleComponents(
            recoveryTaskInstalled,
            rescueTaskInstalled,
            rescueAgentRunning,
            recoveryPending,
            activePhysicalDisplayCount,
            vddInstalled,
            vddEnabled,
            displayActive,
            devices,
            sunshine);
        var evaluation = Evaluate(
            state,
            components,
            errors,
            storageWarning);
        return new BackendLifecycleReport(
            evaluation.Status,
            state.DesiredState,
            preferencePersisted,
            preferencePersisted ? state.UpdatedAtUtc : null,
            components,
            evaluation.Issues,
            storageWarning);
    }

    private static void RestoreAndVerifyPhysicalOnly()
    {
        using var transaction = DisplayTransactionLock.Acquire();
        RestoreAndVerifyPhysicalOnlyLocked(transaction);
    }

    private static void SetManagedVirtualDisplaysWithPhysicalSafety(
        IReadOnlyList<string> instanceIds,
        bool enabled)
    {
        using var transaction = DisplayTransactionLock.Acquire();
        RestoreAndVerifyPhysicalOnlyLocked(transaction);
        try
        {
            DisplayWizardAdapter.SetManagedDriverEnabled(
                transaction,
                instanceIds,
                enabled);
        }
        finally
        {
            // Configuration Manager can mutate the device before returning a
            // failure. Keep the display lease through the PnP operation and
            // always reassert a physical-only topology before releasing it.
            RestoreAndVerifyPhysicalOnlyLocked(transaction);
        }
    }

    private static void RestoreAndVerifyPhysicalOnlyLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        // This is intentionally the first Windows mutation in Disable. If
        // recovery or verification fails, no task or device pause is
        // attempted.
        new SessionManager().RestoreIfPendingLocked(transaction);
        ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
            transaction,
            requireManagedDevice: false);
    }

    private static void TryStep(
        string description,
        Action action,
        ICollection<string> errors)
    {
        try
        {
            action();
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            errors.Add($"Could not {description}: {error.Message}");
        }
    }

    private static T Observe<T>(
        string description,
        Func<T> inspect,
        T fallback,
        ICollection<string> errors)
    {
        try
        {
            return inspect();
        }
        catch (Exception error) when (IsOperationalError(error))
        {
            errors.Add($"Could not inspect {description}: {error.Message}");
            return fallback;
        }
    }

    private static bool IsOperationalError(Exception error) =>
        error is IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            TimeoutException or
            System.ComponentModel.Win32Exception or
            System.Security.SecurityException or
            System.Text.Json.JsonException or
            PlatformNotSupportedException;

    private static string ExecutablePath() =>
        Environment.ProcessPath ??
        Path.Combine(
            AppContext.BaseDirectory,
            "VitaMoonlight.Host.exe");

    private static BackendLifecycleReport ErrorReport(
        string message,
        BackendDesiredState desiredState,
        bool preferencePersisted) => new(
        BackendLifecycleStatus.Error,
        desiredState,
        preferencePersisted,
        null,
        null,
        [message],
        message);
}
