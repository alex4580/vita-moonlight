using VitaMoonlight.Host;

internal static class InstallerMaintenanceContractTests
{
    internal static void Run()
    {
        var began = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        var baseState = new InstallerMaintenanceState(
            FormatVersion: 2,
            OwnerProcessId: 4242,
            OwnerStartedAtUtc: began.AddMinutes(-1),
            BeganAtUtc: began,
            BackendWasEnabled: true,
            RescueAgentTaskWasPresent: true,
            RecoveryTaskWasPresent: true,
            StagedVddAdoptionInstanceId: @"ROOT\DISPLAY\0001",
            Revision: 1);
        var pre0148WireState = baseState with
        {
            StagedVddAdoptionInstanceId = null,
            Revision = 0,
        };
        var backupAdvanced = baseState with
        {
            StagedVddAdoptionInstanceId = @"ROOT\DISPLAY\0002",
            Revision = 2,
        };

        Require(
            InstallerMaintenanceFence.IsValidStateForTest(
                pre0148WireState) &&
            InstallerMaintenanceFence.IsValidStateForTest(baseState) &&
            !InstallerMaintenanceFence.IsValidStateForTest(
                pre0148WireState with
                {
                    StagedVddAdoptionInstanceId =
                        @"ROOT\DISPLAY\0009",
                }) &&
            !InstallerMaintenanceFence.IsValidStateForTest(
                baseState with { FormatVersion = 3 }),
            "The additive staged-identity record is not wire-compatible with the v2 maintenance fence read by an installed <=0.14.8 host.");
        Require(
            InstallerMaintenanceFence.SelectNewestValidStateForTest(
                baseState,
                backupAdvanced,
                anyRecordExists: true) == backupAdvanced,
            "A crash after backup-first candidate publication did not select the newer exact staged identity.");
        Require(
            InstallerMaintenanceFence.SelectNewestValidStateForTest(
                backupAdvanced,
                backupAdvanced,
                anyRecordExists: true) == backupAdvanced,
            "A completed redundant candidate publication did not remain readable.");

        RequireThrows(
            () => InstallerMaintenanceFence.SelectNewestValidStateForTest(
                baseState,
                baseState with
                {
                    StagedVddAdoptionInstanceId =
                        @"ROOT\DISPLAY\0009",
                },
                anyRecordExists: true),
            "Conflicting staged candidates at one revision were accepted.");
        RequireThrows(
            () => InstallerMaintenanceFence.SelectNewestValidStateForTest(
                baseState,
                backupAdvanced with { OwnerProcessId = 7777 },
                anyRecordExists: true),
            "A higher-revision candidate from a foreign maintenance owner replaced the live transaction baseline.");

        Require(
            UninstallManager
                .CanUseUnownedPhysicalOnlyRecoveryFastPathForTest(
                    ownershipJournalExists: false,
                    pendingRecoveryExists: true,
                    exactPhysicalOnlySnapshotAvailable: true) &&
            !UninstallManager
                .CanUseUnownedPhysicalOnlyRecoveryFastPathForTest(
                    ownershipJournalExists: true,
                    pendingRecoveryExists: true,
                    exactPhysicalOnlySnapshotAvailable: true) &&
            !UninstallManager
                .CanUseUnownedPhysicalOnlyRecoveryFastPathForTest(
                    ownershipJournalExists: false,
                    pendingRecoveryExists: true,
                    exactPhysicalOnlySnapshotAvailable: false),
            "A stale display recovery record either blocked a proven physical-only unowned-device upgrade or broadened it without proof.");
        Require(
            UninstallManager.ShouldAttemptManagedVddReleaseForTest(
                restoreManagedVdd: true,
                ownershipJournalExists: true) &&
            !UninstallManager.ShouldAttemptManagedVddReleaseForTest(
                restoreManagedVdd: true,
                ownershipJournalExists: false) &&
            !UninstallManager.ShouldAttemptManagedVddReleaseForTest(
                restoreManagedVdd: false,
                ownershipJournalExists: true),
            "No-journal uninstall could still locate or invoke driver-release tools without exact ownership authority.");

        // An interrupted upgrade must reconcile from the durable v2 baseline
        // rather than the tasks which happen to remain after the crash. Test
        // every original enabled-state combination so retry cannot silently
        // manufacture or discard a safeguard obligation.
        foreach (var rescueWasPresent in new[] { false, true })
        {
            foreach (var recoveryWasPresent in new[] { false, true })
            {
                var restore = InstallerMaintenanceFence
                    .GetSafeguardRestorePlanForTest(
                        backendIsEnabled: true,
                        backendWasEnabled: true,
                        rescueAgentTaskWasPresent: rescueWasPresent,
                        recoveryTaskWasPresent: recoveryWasPresent);
                Require(
                    restore.RescueAgent == rescueWasPresent &&
                    restore.RecoveryTask == recoveryWasPresent,
                    "Enabled upgrade retry did not preserve the exact durable safeguard baseline.");

                var paused = InstallerMaintenanceFence
                    .GetSafeguardRestorePlanForTest(
                        backendIsEnabled: false,
                        backendWasEnabled: true,
                        rescueAgentTaskWasPresent: rescueWasPresent,
                        recoveryTaskWasPresent: recoveryWasPresent);
                Require(
                    !paused.RescueAgent && !paused.RecoveryTask,
                    "A newer Paused preference could recreate a background safeguard during cancel rollback.");
            }
        }

        var enabledAfterPausedSnapshot = InstallerMaintenanceFence
            .GetSafeguardRestorePlanForTest(
                backendIsEnabled: true,
                backendWasEnabled: false,
                rescueAgentTaskWasPresent: false,
                recoveryTaskWasPresent: false);
        Require(
            enabledAfterPausedSnapshot.RescueAgent &&
            enabledAfterPausedSnapshot.RecoveryTask,
            "A newer Enable preference did not restore both standard safeguards after a paused snapshot.");

        Require(
            !InstallerMaintenanceFence.ShouldReconcileRescueForTest(
                rescueTaskPresent: false,
                installedExecutableAvailable: false) &&
            InstallerMaintenanceFence.ShouldReconcileRescueForTest(
                rescueTaskPresent: true,
                installedExecutableAvailable: false) &&
            InstallerMaintenanceFence.ShouldReconcileRescueForTest(
                rescueTaskPresent: false,
                installedExecutableAvailable: true) &&
            InstallerMaintenanceFence.ShouldReconcileRescueForTest(
                rescueTaskPresent: true,
                installedExecutableAvailable: true),
            "Rescue reconciliation can either leak an owned firewall rule from an installed host or touch a genuine clean install with no task and no executable.");

        Require(
            !InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: true,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Missing,
                ExactScheduledTaskState.Present) &&
            InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: true,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Present,
                ExactScheduledTaskState.Present) &&
            !InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: false,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Present,
                ExactScheduledTaskState.Missing) &&
            InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: false,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Missing,
                ExactScheduledTaskState.Missing),
            "Cancel/retry could clear its fence before exact enabled restoration or paused cleanup completed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
