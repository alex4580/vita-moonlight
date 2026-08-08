using VitaMoonlight.Host;

internal static class ManagedVddOwnershipContractTests
{
    internal static void Run()
    {
        var now = DateTimeOffset.Parse("2026-08-08T12:00:00Z");
        var create = ManagedVddOwnershipJournal.ClassifyInstall(
            null,
            [],
            now);
        Require(
            create.Action == ManagedVddInstallAction.CreateInstance &&
            create.State.PendingCreation is not null &&
            create.State.Device is null,
            "A clean install did not publish creation intent before device creation.");

        var created = ManagedVddOwnershipJournal.CompleteCreation(
            create.State,
            [Device(@"ROOT\DISPLAY\0007", enabled: true)],
            now.AddSeconds(1));
        Require(
            created.Device is
            {
                InstanceId: @"ROOT\DISPLAY\0007",
                Ownership: ManagedVddOwnershipKind.AppCreated,
                PriorEnabled: null,
                LastKnownEnabled: true,
            } &&
            created.PendingCreation is null,
            "A newly created exact instance was not recorded as app-created.");
        Require(
            ManagedVddOwnershipJournal.ClassifyInstall(
                created,
                [],
                now.AddMinutes(1)).Action ==
                ManagedVddInstallAction.CreateInstance,
            "Repair did not publish a new creation intent for a missing app-created node with zero present candidates.");

        var adopted = ManagedVddOwnershipJournal.ClassifyInstall(
            null,
            [Device(@"ROOT\DISPLAY\0002", enabled: false)],
            now,
            new ManagedVddAcquisitionPolicy(
                ExactLegacyVitaOwnershipEvidence: false,
                ExpectedExistingDeviceInstanceId:
                    @"ROOT\DISPLAY\0002"));
        Require(
            adopted.Device is
            {
                InstanceId: @"ROOT\DISPLAY\0002",
                Ownership: ManagedVddOwnershipKind.Adopted,
                PriorEnabled: false,
                LastKnownEnabled: false,
            },
            "An unambiguous pre-existing instance did not retain its exact disabled baseline.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                adopted.State,
                [],
                now.AddMinutes(1)),
            "Repair substituted a newly created node for a missing adopted instance.");

        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                null,
                [Device(@"ROOT\DISPLAY\0003", enabled: true)],
                now),
            "A device which appeared after a clean pre-copy query was silently claimed from newly copied executable evidence instead of requiring a new exact adoption decision.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                null,
                [Device(@"ROOT\DISPLAY\0042", enabled: true)],
                required: false),
            "Optional idle recovery proceeded toward topology mutation with an unrelated journal-less MTT node.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                null,
                [Device(@"ROOT\DISPLAY\0042", enabled: true)],
                required: true),
            "Required stream readiness accepted an unrelated journal-less MTT node.");
        Require(
            !ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                create.State,
                [],
                required: false),
            "A zero-present creation intent blocked optional pre-install physical recovery.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                create.State,
                [Device(@"ROOT\DISPLAY\0043", enabled: true)],
                required: false),
            "Optional recovery guessed ownership after a pending creation acquired a present candidate.");
        var migratedLegacy = ManagedVddOwnershipJournal.ClassifyInstall(
            null,
            [Device(@"ROOT\DISPLAY\0003", enabled: true)],
            now,
            new ManagedVddAcquisitionPolicy(
                ExactLegacyVitaOwnershipEvidence: true,
                ExpectedExistingDeviceInstanceId: null));
        Require(
            migratedLegacy.Device is
            {
                Ownership: ManagedVddOwnershipKind.AppCreated,
                PriorEnabled: null,
                LastKnownEnabled: true,
            },
            "A proven pre-journal Vita node learned the bug-induced enabled state as an adopted uninstall baseline.");
        Require(
            ManagedVddOwnershipJournal.ClassifyLegacyMigration(
                [Device(@"ROOT\DISPLAY\0044", enabled: true)],
                now,
                exactLegacyVitaOwnershipEvidence: false) is null,
            "Installer maintenance treated an unproven pre-existing MTT node as a failed legacy migration instead of leaving it untouched for an explicit adoption decision.");
        var provenMigration =
            ManagedVddOwnershipJournal.ClassifyLegacyMigration(
                [Device(@"ROOT\DISPLAY\0045", enabled: false)],
                now,
                exactLegacyVitaOwnershipEvidence: true);
        Require(
            provenMigration?.Device is
            {
                InstanceId: @"ROOT\DISPLAY\0045",
                Ownership: ManagedVddOwnershipKind.AppCreated,
                PriorEnabled: null,
                LastKnownEnabled: false,
            },
            "Installer maintenance did not migrate an exact proven legacy Vita node as app-created ownership.");
        Require(
            ManagedVddOwnershipJournal.EvaluateAdoptionRequirement(
                null,
                []) is
                { RequiresExplicitAdoption: false } &&
            ManagedVddOwnershipJournal.EvaluateAdoptionRequirement(
                null,
                [Device(@"ROOT\DISPLAY\0004", enabled: false)]) is
                {
                    RequiresExplicitAdoption: true,
                    CandidateInstanceId: @"ROOT\DISPLAY\0004",
                },
            "Read-only setup readiness did not require exact adoption for every present journal-less node after the exclusive pre-copy migration opportunity.");

        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                null,
                [Device(@"ROOT\DISPLAY\0099", enabled: false)],
                now,
                new ManagedVddAcquisitionPolicy(
                    ExactLegacyVitaOwnershipEvidence: false,
                    ExpectedExistingDeviceInstanceId:
                        @"ROOT\DISPLAY\0004")),
            "Explicit adoption followed a replacement device instead of remaining bound to the exact candidate shown for consent.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                null,
                [],
                now,
                new ManagedVddAcquisitionPolicy(
                    ExactLegacyVitaOwnershipEvidence: false,
                    ExpectedExistingDeviceInstanceId:
                        @"ROOT\DISPLAY\0004")),
            "Explicit adoption silently became clean device creation after its approved candidate disappeared.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                null,
                [
                    Device(@"ROOT\DISPLAY\0004", enabled: false),
                    Device(@"ROOT\DISPLAY\0099", enabled: false),
                ],
                now,
                new ManagedVddAcquisitionPolicy(
                    ExactLegacyVitaOwnershipEvidence: false,
                    ExpectedExistingDeviceInstanceId:
                        @"ROOT\DISPLAY\0004")),
            "Explicit adoption ignored a second present candidate that appeared after consent.");
        var approvedExisting = ManagedVddOwnershipJournal.ClassifyInstall(
            adopted.State,
            [Device(@"ROOT\DISPLAY\0002", enabled: false)],
            now,
            new ManagedVddAcquisitionPolicy(
                ExactLegacyVitaOwnershipEvidence: false,
                ExpectedExistingDeviceInstanceId:
                    @"ROOT\DISPLAY\0002"));
        Require(
            approvedExisting.Device?.Ownership ==
                ManagedVddOwnershipKind.Adopted &&
            approvedExisting.Device.InstanceId ==
                @"ROOT\DISPLAY\0002",
            "A deferred exact adoption retry rejected its already-adopted matching state.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                created,
                [Device(@"ROOT\DISPLAY\0007", enabled: true)],
                now,
                new ManagedVddAcquisitionPolicy(
                    ExactLegacyVitaOwnershipEvidence: false,
                    ExpectedExistingDeviceInstanceId:
                        @"ROOT\DISPLAY\0007")),
            "Exact adoption consent silently accepted an AppCreated state manufactured by another path.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                adopted.State,
                [Device(@"ROOT\DISPLAY\0002", enabled: false)],
                now,
                new ManagedVddAcquisitionPolicy(
                    ExactLegacyVitaOwnershipEvidence: false,
                    ExpectedExistingDeviceInstanceId:
                        @"ROOT\DISPLAY\0099")),
            "Exact adoption consent silently accepted an already-adopted different instance.");

        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                null,
                [
                    Device(@"ROOT\DISPLAY\0001", enabled: true),
                    Device(@"ROOT\DISPLAY\0002", enabled: false),
                ],
                now),
            "Install adopted an ambiguous set of pre-existing MTT instances.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.ClassifyInstall(
                created,
                [
                    Device(@"ROOT\DISPLAY\0007", enabled: true),
                    Device(@"ROOT\DISPLAY\0099", enabled: true),
                ],
                now),
            "Repair ignored an unowned present MTT instance.");
        Require(
            ManagedVddOwnershipJournal
                .SelectOwnedEnabledRecoveryTargetForTest(
                    created,
                    [Device(@"ROOT\DISPLAY\0007", enabled: true)]) ==
                @"ROOT\DISPLAY\0007",
            "Recovery did not select the exact enabled journal-owned instance.");
        RequireThrows(
            () => ManagedVddOwnershipJournal
                .SelectOwnedEnabledRecoveryTargetForTest(
                    created,
                    [
                        Device(@"ROOT\DISPLAY\0007", enabled: true),
                        Device(@"ROOT\DISPLAY\0099", enabled: true),
                    ]),
            "Recovery could restart a sole-looking target while an unowned MTT node was present.");
        RequireThrows(
            () => ManagedVddOwnershipJournal
                .SelectOwnedEnabledRecoveryTargetForTest(
                    created with
                    {
                        Device = created.Device! with
                        {
                            LastKnownEnabled = false,
                        },
                    },
                    [Device(@"ROOT\DISPLAY\0007", enabled: false)]),
            "Recovery could restart the exact journal-owned instance while it was disabled.");

        var adoptedState = adopted.State;
        RequireThrows(
            () => ManagedVddOwnershipJournal.BeginEnabledMutationForTest(
                adoptedState,
                currentEnabled: true,
                desiredEnabled: false),
            "A runtime toggle overwrote an out-of-band enabled-state change.");

        var pendingEnable = ManagedVddOwnershipJournal
            .BeginEnabledMutationForTest(
                adoptedState,
                currentEnabled: false,
                desiredEnabled: true);
        Require(
            pendingEnable.Device?.PendingMutation ==
                new ManagedVddEnabledMutation(false, true),
            "A device toggle was not preceded by durable compare-and-write intent.");
        var recoveredMutation = ManagedVddOwnershipJournal.ClassifyInstall(
            pendingEnable,
            [Device(@"ROOT\DISPLAY\0002", enabled: true)],
            now.AddSeconds(2));
        Require(
            recoveredMutation.Device is
            {
                LastKnownEnabled: true,
                PendingMutation: null,
            },
            "A completed toggle was not recovered from its durable intent.");

        var restoreAdopted = ManagedVddOwnershipJournal.ClassifyRelease(
            adoptedState with
            {
                Device = adoptedState.Device! with
                {
                    PriorEnabled = true,
                },
            },
            [Device(@"ROOT\DISPLAY\0002", enabled: false)],
            now);
        Require(
            restoreAdopted.Action ==
                ManagedVddReleaseAction.RestoreAdoptedInstance &&
            restoreAdopted.DesiredEnabled == true,
            "Uninstall did not restore an adopted instance's exact prior enabled state.");

        var preserveConcurrent = ManagedVddOwnershipJournal.ClassifyRelease(
            adoptedState,
            [Device(@"ROOT\DISPLAY\0002", enabled: true)],
            now);
        Require(
            preserveConcurrent.Action ==
                ManagedVddReleaseAction.PreserveConcurrentChange,
            "Uninstall overwrote an enabled-state change made by another controller.");

        var removeCreated = ManagedVddOwnershipJournal.ClassifyRelease(
            created,
            [Device(@"ROOT\DISPLAY\0007", enabled: true)],
            now);
        Require(
            removeCreated.Action ==
                ManagedVddReleaseAction.RemoveAppCreatedInstance,
            "Uninstall retained an unchanged app-created device instance.");
        var alreadyRemoved = ManagedVddOwnershipJournal.ClassifyRelease(
            created,
            [],
            now);
        Require(
            alreadyRemoved.Action == ManagedVddReleaseAction.Nothing,
            "Uninstall could not complete idempotently after its exact app-created node disappeared.");

        var releasedAdopted = adoptedState with
        {
            Revision = adoptedState.Revision + 1,
            ReleasedAtUtc = now,
            ReleasedBy = ManagedVddReleaseAction.RestoreAdoptedInstance,
        };
        Require(
            ManagedVddOwnershipJournal.EvaluateAdoptionRequirement(
                releasedAdopted,
                [Device(@"ROOT\DISPLAY\0002", enabled: false)]) is
                { RequiresExplicitAdoption: true },
            "Read-only setup readiness silently reclaimed a released adopted node from stale legacy evidence.");
        Require(
            !ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                releasedAdopted,
                [Device(@"ROOT\DISPLAY\0002", enabled: false)],
                required: false),
            "An uninstall tombstone still authorized runtime control of a released adopted node.");
        RequireThrows(
            () => ManagedVddOwnershipJournal.HasOwnedPresentDeviceForTest(
                releasedAdopted,
                [Device(@"ROOT\DISPLAY\0002", enabled: false)],
                required: true),
            "Streaming accepted a released adopted-node tombstone as active ownership.");
        var reactivatedAdopted = ManagedVddOwnershipJournal
            .ReactivateReleasedForTest(
                releasedAdopted,
                [Device(@"ROOT\DISPLAY\0002", enabled: false)]);
        Require(
            reactivatedAdopted is
            {
                ReleasedAtUtc: null,
                ReleasedBy: null,
            } &&
            reactivatedAdopted.Device?.Ownership ==
                ManagedVddOwnershipKind.Adopted,
            "Failed-uninstall rollback could not reactivate an intact adopted-node ownership record.");

        var releasedCreated = created with
        {
            Revision = created.Revision + 1,
            ReleasedAtUtc = now,
            ReleasedBy =
                ManagedVddReleaseAction.RemoveAppCreatedInstance,
        };
        Require(
            ManagedVddOwnershipJournal.ReactivateReleasedForTest(
                releasedCreated,
                []) is null,
            "Failed-uninstall rollback guessed a replacement for an already-removed app-created node.");
        var releasedConcurrent = releasedAdopted with
        {
            ReleasedBy =
                ManagedVddReleaseAction.PreserveConcurrentChange,
        };
        Require(
            ManagedVddOwnershipJournal.ReactivateReleasedForTest(
                releasedConcurrent,
                [Device(@"ROOT\DISPLAY\0002", enabled: true)]) is null,
            "Failed-uninstall rollback reclaimed a node relinquished to preserve a concurrent change.");
    }

    private static ManagedVddDeviceStatus Device(
        string instanceId,
        bool enabled) =>
        new(
            instanceId,
            Present: true,
            Enabled: enabled,
            DeviceStatus: enabled ? 0x00000008u : 0u,
            ProblemCode: enabled ? 0u : 22u);

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
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
