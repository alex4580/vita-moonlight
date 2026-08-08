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
