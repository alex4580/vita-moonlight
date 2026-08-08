using System.Text.Json;
using System.Text.Json.Serialization;

namespace VitaMoonlight.Host;

internal enum ManagedVddOwnershipKind
{
    AppCreated,
    Adopted,
}

internal enum ManagedVddInstallAction
{
    UseOwnedInstance,
    CreateInstance,
}

internal enum ManagedVddReleaseAction
{
    Nothing,
    RemoveAppCreatedInstance,
    RestoreAdoptedInstance,
    PreserveConcurrentChange,
}

internal enum ManagedVddRollbackDisposition
{
    NoReleasedOwnership,
    Reactivated,
    RetainedTombstone,
}

internal sealed record ManagedVddEnabledMutation(
    bool ExpectedEnabled,
    bool DesiredEnabled);

internal sealed record ManagedVddOwnedInstance(
    string InstanceId,
    ManagedVddOwnershipKind Ownership,
    bool? PriorEnabled,
    bool LastKnownEnabled,
    ManagedVddEnabledMutation? PendingMutation,
    DateTimeOffset AcquiredAtUtc);

internal sealed record ManagedVddCreationIntent(
    IReadOnlyList<string> PresentInstanceIdsBeforeCreate,
    DateTimeOffset RequestedAtUtc);

internal sealed record ManagedVddOwnershipState(
    int FormatVersion,
    long Revision,
    ManagedVddOwnedInstance? Device,
    ManagedVddCreationIntent? PendingCreation,
    DateTimeOffset? ReleasedAtUtc = null,
    ManagedVddReleaseAction? ReleasedBy = null);

internal sealed record ManagedVddInstallPlan(
    ManagedVddInstallAction Action,
    ManagedVddOwnershipState State,
    ManagedVddOwnedInstance? Device);

internal sealed record ManagedVddAcquisitionPolicy(
    bool ExactLegacyVitaOwnershipEvidence,
    string? ExpectedExistingDeviceInstanceId);

internal sealed record ManagedVddAdoptionReadiness(
    bool RequiresExplicitAdoption,
    string Reason,
    string? CandidateInstanceId);

internal sealed record ManagedVddReleasePlan(
    ManagedVddReleaseAction Action,
    ManagedVddOwnershipState? State,
    ManagedVddOwnedInstance? Device,
    bool? DesiredEnabled);

/// <summary>
/// Durable authority for the one exact ROOT\MttVDD device instance Vita
/// Moonlight is allowed to control. A missing journal never grants authority
/// over a pre-existing node. Explicit install/repair may conservatively adopt
/// one unambiguous node and records its enabled state before changing it.
/// </summary>
internal static class ManagedVddOwnershipJournal
{
    internal const int CurrentFormatVersion = 1;
    private const int MaximumJournalBytes = 32 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string JournalFile => Path.Combine(
        HostStatePaths.Root,
        "managed-vdd-ownership.json");

    internal static bool RequiresExplicitAdoption(
        out string reason,
        out string? candidateInstanceId)
    {
        MachineStateSecurity.Secure();
        if (DisplayWizardAdapter.IsLegacyDriverInstalled())
        {
            throw new InvalidOperationException(
                "Windows has an unowned legacy ROOT\\IddSampleDriver virtual display. Resolve it before configuring the Vita display.");
        }
        var state = LoadCore();
        var devices = DisplayWizardAdapter.InspectManagedDriverDevices();
        var readiness = EvaluateAdoptionRequirement(
            state,
            devices);
        reason = readiness.Reason;
        candidateInstanceId = readiness.CandidateInstanceId;
        return readiness.RequiresExplicitAdoption;
    }

    internal static ManagedVddAdoptionReadiness
        EvaluateAdoptionRequirement(
            ManagedVddOwnershipState? state,
            IEnumerable<ManagedVddDeviceStatus> observedDevices)
    {
        var devices = NormalizeDevices(observedDevices);
        var present = devices.Where(device => device.Present).ToArray();
        if (state is not null)
        {
            ValidateState(state);
            if (state.ReleasedAtUtc is null &&
                state.PendingCreation is null)
            {
                RequireNoUnownedPresentDevices(state.Device!, devices);
                return new ManagedVddAdoptionReadiness(
                    false,
                    "The exact managed-VDD instance is already journal-owned.",
                    null);
            }
            if (state.PendingCreation is not null)
            {
                if (present.Length > 1)
                {
                    throw Ambiguous(present.Length);
                }
                return new ManagedVddAdoptionReadiness(
                    false,
                    present.Length == 0
                        ? "Managed-VDD creation is prepared and no device is present yet."
                        : "Managed-VDD creation is prepared and repair can finalize its single candidate.",
                    null);
            }
            // A released tombstone deliberately does not auto-reclaim its
            // former node.
        }

        if (present.Length == 0)
        {
            return new ManagedVddAdoptionReadiness(
                false,
                "No existing managed-VDD instance needs adoption.",
                null);
        }
        if (present.Length != 1)
        {
            throw Ambiguous(present.Length);
        }
        return new ManagedVddAdoptionReadiness(
            true,
            "The sole managed-VDD instance has no active ownership journal or exact protected legacy Vita footprint. Explicit adoption is required to preserve its current enabled state as the uninstall baseline.",
            present[0].InstanceId);
    }

    internal static ManagedVddInstallPlan PrepareInstallLocked(
        DisplayTransactionLease transaction,
        string? expectedExistingDeviceInstanceId = null)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore();
        var devices = DisplayWizardAdapter.InspectManagedDriverDevices();
        var plan = ClassifyInstall(
            state,
            devices,
            DateTimeOffset.UtcNow,
            new ManagedVddAcquisitionPolicy(
                // Exact legacy acquisition is intentionally exclusive to the
                // pre-copy installer-maintenance migration. Once setup has
                // copied the new executable, that file can no longer prove
                // Vita created a pre-existing device.
                ExactLegacyVitaOwnershipEvidence: false,
                expectedExistingDeviceInstanceId));
        if (!Equals(state, plan.State))
        {
            SaveCore(plan.State);
        }
        return plan;
    }

    /// <summary>
    /// Upgrade-only bridge for releases which predate the instance journal.
    /// The enabled bit is recorded only as the compare-and-write starting
    /// point; it is never treated as a restore baseline. Without the complete
    /// protected prior-product footprint, a present node remains unowned.
    /// </summary>
    internal static bool MigrateLegacyOwnershipIfProvenLocked(
        DisplayTransactionLease transaction,
        bool allowAuthorizedMaintenanceBootstrap = false)
    {
        transaction.RequireActive();
        SecureMigrationLocked(transaction);
        if (LoadCore() is not null) return false;
        var devices = DisplayWizardAdapter.InspectManagedDriverDevices();
        var present = devices.Where(device => device.Present).ToArray();
        if (present.Length == 0) return false;
        var evidence = DisplayWizardAdapter
            .HasExactLegacyVitaOwnershipEvidence(
                devices,
                allowAuthorizedMaintenanceBootstrap);
        var plan = ClassifyLegacyMigration(
            devices,
            DateTimeOffset.UtcNow,
            evidence);
        if (plan is null) return false;
        SaveCore(plan.State);
        return plan.Device?.Ownership ==
            ManagedVddOwnershipKind.AppCreated;
    }

    /// <summary>
    /// Classifies the narrow pre-journal migration without turning a failed
    /// proof into an installation error. An unproven MTT node may be perfectly
    /// legitimate third-party state, so installer maintenance must leave it
    /// untouched and ask for explicit adoption later if virtual-display setup
    /// was selected.
    /// </summary>
    internal static ManagedVddInstallPlan? ClassifyLegacyMigration(
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        DateTimeOffset now,
        bool exactLegacyVitaOwnershipEvidence)
    {
        if (!exactLegacyVitaOwnershipEvidence) return null;
        return ClassifyInstall(
            null,
            observedDevices,
            now,
            new ManagedVddAcquisitionPolicy(
                ExactLegacyVitaOwnershipEvidence: true,
                ExpectedExistingDeviceInstanceId: null));
    }

    internal static ManagedVddOwnedInstance CompleteCreationLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore()
            ?? throw new InvalidDataException(
                "The managed-VDD creation intent is missing.");
        ValidateState(state);
        if (state.PendingCreation is null || state.Device is not null)
        {
            throw new InvalidDataException(
                "The managed-VDD journal does not contain a pending creation intent.");
        }

        var completed = CompleteCreation(
            state,
            DisplayWizardAdapter.InspectManagedDriverDevices(),
            DateTimeOffset.UtcNow);
        SaveCore(completed);
        return completed.Device!;
    }

    internal static IReadOnlyList<ManagedVddDeviceStatus>
        RequireOwnedPresentDevicesLocked(
            DisplayTransactionLease transaction,
            bool required)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        return RequireOwnedPresentDevicesCore(required);
    }

    internal static IReadOnlyList<ManagedVddDeviceStatus>
        RequireOwnedPresentDevices(bool required)
    {
        MachineStateSecurity.Secure();
        return RequireOwnedPresentDevicesCore(required);
    }

    internal static string RequireOwnedEnabledRecoveryTargetLocked(
        DisplayTransactionLease transaction)
    {
        var devices = RequireOwnedPresentDevicesLocked(
            transaction,
            required: true);
        return SelectEnabledRecoveryTarget(devices);
    }

    internal static bool PrepareEnabledMutationLocked(
        DisplayTransactionLease transaction,
        string instanceId,
        bool desiredEnabled)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore()
            ?? throw MissingOwnership();
        var resolution = ResolveOwnedPresentDevice(
            state,
            DisplayWizardAdapter.InspectManagedDriverDevices(),
            required: true,
            preserveConcurrentChange: false);
        if (!InstanceIdsEqual(
                resolution.Device!.InstanceId,
                instanceId))
        {
            throw new InvalidOperationException(
                "The requested managed virtual display is not the exact journal-owned instance.");
        }

        state = resolution.State!;
        var owned = resolution.Device;
        if (!Equals(LoadCore(), state))
        {
            SaveCore(state);
        }
        if (owned.LastKnownEnabled == desiredEnabled)
        {
            return false;
        }

        var pending = owned with
        {
            PendingMutation = new ManagedVddEnabledMutation(
                owned.LastKnownEnabled,
                desiredEnabled),
        };
        SaveCore(Next(state, pending));
        return true;
    }

    internal static void CompleteEnabledMutationLocked(
        DisplayTransactionLease transaction,
        string instanceId,
        bool desiredEnabled)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore()
            ?? throw MissingOwnership();
        ValidateState(state);
        var owned = state.Device
            ?? throw new InvalidDataException(
                "The managed-VDD journal has no owned device.");
        if (!InstanceIdsEqual(owned.InstanceId, instanceId) ||
            owned.PendingMutation is not { } pending ||
            pending.DesiredEnabled != desiredEnabled)
        {
            throw new InvalidDataException(
                "The managed-VDD enabled-state completion does not match its durable intent.");
        }

        var devices = NormalizeDevices(
            DisplayWizardAdapter.InspectManagedDriverDevices());
        RequireNoUnownedPresentDevices(owned, devices);
        var current = devices.SingleOrDefault(device =>
            device.Present &&
            InstanceIdsEqual(device.InstanceId, owned.InstanceId));
        if (current is null || current.Enabled != desiredEnabled)
        {
            throw new InvalidOperationException(
                "Windows did not confirm the journal-owned virtual display's intended enabled state.");
        }

        SaveCore(Next(
            state,
            owned with
            {
                LastKnownEnabled = desiredEnabled,
                PendingMutation = null,
            }));
    }

    internal static ManagedVddReleasePlan PrepareReleaseLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        if (DisplayWizardAdapter.IsLegacyDriverInstalled())
        {
            throw new InvalidOperationException(
                "Windows has an unowned legacy ROOT\\IddSampleDriver virtual display. No virtual display device was changed.");
        }
        var state = LoadCore();
        var plan = ClassifyRelease(
            state,
            DisplayWizardAdapter.InspectManagedDriverDevices(),
            DateTimeOffset.UtcNow);
        if (!Equals(state, plan.State) && plan.State is not null)
        {
            SaveCore(plan.State);
        }
        return plan;
    }

    internal static void CompleteReleaseLocked(
        DisplayTransactionLease transaction,
        string? expectedInstanceId,
        ManagedVddReleaseAction releasedBy)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore();
        if (state?.Device is { } owned &&
            expectedInstanceId is not null &&
            !InstanceIdsEqual(owned.InstanceId, expectedInstanceId))
        {
            throw new InvalidOperationException(
                "The managed-VDD ownership changed during release; its journal was retained.");
        }
        if (state?.Device is not { } device)
        {
            TrustedFileSystem.DeleteFile(JournalFile);
            return;
        }
        if (state.ReleasedAtUtc is not null)
        {
            return;
        }
        SaveCore(state with
        {
            Revision = checked(state.Revision + 1),
            Device = device with { PendingMutation = null },
            ReleasedAtUtc = DateTimeOffset.UtcNow,
            ReleasedBy = releasedBy,
        });
    }

    internal static ManagedVddRollbackDisposition
        ReactivateReleasedOwnershipLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        SecureLocked(transaction);
        var state = LoadCore();
        var devices = NormalizeDevices(
            DisplayWizardAdapter.InspectManagedDriverDevices());
        var reactivated = ReactivateReleasedForTest(state, devices);
        if (state?.ReleasedAtUtc is null)
        {
            return ManagedVddRollbackDisposition.NoReleasedOwnership;
        }
        if (reactivated is null)
        {
            return ManagedVddRollbackDisposition.RetainedTombstone;
        }
        SaveCore(reactivated);
        return ManagedVddRollbackDisposition.Reactivated;
    }

    internal static ManagedVddOwnershipState? ReactivateReleasedForTest(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices)
    {
        if (state?.ReleasedAtUtc is null || state.Device is not { } owned)
        {
            return null;
        }
        ValidateState(state);
        if (state.ReleasedBy ==
            ManagedVddReleaseAction.PreserveConcurrentChange)
        {
            // Relinquishment protected another controller's change. A failed
            // product uninstall must retain the retry-safe tombstone rather
            // than silently reclaiming that device.
            return null;
        }
        var devices = NormalizeDevices(observedDevices);
        RequireNoUnownedPresentDevices(owned, devices);
        var current = devices.SingleOrDefault(device =>
            device.Present &&
            InstanceIdsEqual(device.InstanceId, owned.InstanceId));
        if (current is null)
        {
            // An app-created instance which was already removed cannot be
            // recreated safely as part of rollback. Retain the tombstone so
            // uninstall can be retried idempotently.
            return null;
        }
        return state with
        {
            Revision = checked(state.Revision + 1),
            Device = owned with
            {
                LastKnownEnabled = current.Enabled,
                PendingMutation = null,
            },
            ReleasedAtUtc = null,
            ReleasedBy = null,
        };
    }

    internal static bool HasOwnedPresentDeviceForTest(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        bool required) =>
        ResolveOwnedPresentDevice(
            state,
            observedDevices,
            required,
            preserveConcurrentChange: false).Device is not null;

    internal static string SelectOwnedEnabledRecoveryTargetForTest(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices)
    {
        var resolution = ResolveOwnedPresentDevice(
            state,
            observedDevices,
            required: true,
            preserveConcurrentChange: false);
        return SelectEnabledRecoveryTarget(
            resolution.DeviceStatus is null
                ? []
                : [resolution.DeviceStatus]);
    }

    private static string SelectEnabledRecoveryTarget(
        IReadOnlyList<ManagedVddDeviceStatus> devices)
    {
        if (devices.Count != 1 ||
            !devices[0].Present ||
            !devices[0].Enabled)
        {
            throw new InvalidOperationException(
                "Vita Moonlight will not restart a virtual display during recovery because the exact journal-owned device is not present and enabled. No unrelated device was changed.");
        }
        return devices[0].InstanceId;
    }

    internal static ManagedVddInstallPlan ClassifyInstall(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        DateTimeOffset now,
        ManagedVddAcquisitionPolicy? acquisitionPolicy = null)
    {
        acquisitionPolicy ??= new ManagedVddAcquisitionPolicy(
            ExactLegacyVitaOwnershipEvidence: false,
            ExpectedExistingDeviceInstanceId: null);
        var devices = NormalizeDevices(observedDevices);
        var present = devices.Where(device => device.Present).ToArray();
        if (state is null)
        {
            return ClassifyUnownedInstall(
                present,
                now,
                acquisitionPolicy,
                revision: 1);
        }

        ValidateState(state);
        if (acquisitionPolicy.ExpectedExistingDeviceInstanceId is
                { } expectedExisting &&
            state.ReleasedAtUtc is null)
        {
            var existingAdopted = state.Device;
            if (state.PendingCreation is not null ||
                existingAdopted?.Ownership !=
                    ManagedVddOwnershipKind.Adopted ||
                !InstanceIdsEqual(
                    existingAdopted?.InstanceId ?? string.Empty,
                    expectedExisting))
            {
                throw new InvalidOperationException(
                    "The protected managed-VDD ownership state does not match the exact adopted instance approved by the user. No device was substituted or changed.");
            }
        }
        if (state.ReleasedAtUtc is not null)
        {
            return ClassifyUnownedInstall(
                present,
                now,
                acquisitionPolicy with
                {
                    // A tombstone is current-format evidence, not evidence
                    // that an older Vita release created a remaining node.
                    ExactLegacyVitaOwnershipEvidence = false,
                },
                checked(state.Revision + 1));
        }
        if (state.PendingCreation is not null)
        {
            if (present.Length == 0)
            {
                return new ManagedVddInstallPlan(
                    ManagedVddInstallAction.CreateInstance,
                    state,
                    null);
            }
            var completed = CompleteCreation(state, devices, now);
            return new ManagedVddInstallPlan(
                ManagedVddInstallAction.UseOwnedInstance,
                completed,
                completed.Device);
        }

        if (present.Length == 0 &&
            state.Device?.Ownership ==
                ManagedVddOwnershipKind.AppCreated)
        {
            // The exact app-created node disappeared, but no present node can
            // be mistaken for it. Publish a fresh creation intent before
            // repair; an adopted node is never substituted this way because
            // its external owner's identity/baseline would be lost.
            var pending = new ManagedVddOwnershipState(
                CurrentFormatVersion,
                checked(state.Revision + 1),
                null,
                new ManagedVddCreationIntent([], now));
            return new ManagedVddInstallPlan(
                ManagedVddInstallAction.CreateInstance,
                pending,
                null);
        }

        var resolved = ResolveOwnedPresentDevice(
            state,
            devices,
            required: true,
            preserveConcurrentChange: false);
        return new ManagedVddInstallPlan(
            ManagedVddInstallAction.UseOwnedInstance,
            resolved.State!,
            resolved.Device);
    }

    internal static ManagedVddOwnershipState CompleteCreation(
        ManagedVddOwnershipState state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        DateTimeOffset now)
    {
        ValidateState(state);
        var intent = state.PendingCreation
            ?? throw new InvalidDataException(
                "The managed-VDD journal has no pending creation intent.");
        var devices = NormalizeDevices(observedDevices);
        var present = devices.Where(device => device.Present).ToArray();
        var previous = intent.PresentInstanceIdsBeforeCreate
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = present
            .Where(device => !previous.Contains(device.InstanceId))
            .ToArray();
        if (created.Length != 1 || present.Length != created.Length)
        {
            throw new InvalidOperationException(
                "Vita Moonlight could not prove which virtual display instance was created. " +
                $"Windows exposed {created.Length} new and {present.Length} total present ROOT\\MttVDD nodes; no node was adopted or changed.");
        }

        var owned = new ManagedVddOwnedInstance(
            created[0].InstanceId,
            ManagedVddOwnershipKind.AppCreated,
            null,
            created[0].Enabled,
            null,
            now);
        return new ManagedVddOwnershipState(
            CurrentFormatVersion,
            checked(state.Revision + 1),
            owned,
            null);
    }

    private static ManagedVddInstallPlan ClassifyUnownedInstall(
        IReadOnlyList<ManagedVddDeviceStatus> present,
        DateTimeOffset now,
        ManagedVddAcquisitionPolicy acquisitionPolicy,
        long revision)
    {
        var expectedExisting =
            acquisitionPolicy.ExpectedExistingDeviceInstanceId;
        if (expectedExisting is not null)
        {
            if (!IsValidInstanceId(expectedExisting))
            {
                throw new InvalidDataException(
                    "The explicitly approved managed-VDD candidate identity is invalid.");
            }
            if (present.Count != 1 ||
                !InstanceIdsEqual(
                    present[0].InstanceId,
                    expectedExisting))
            {
                throw new InvalidOperationException(
                    "The exact virtual-display device approved for adoption is no longer the sole present ROOT\\MttVDD instance. " +
                    $"Expected {expectedExisting}; observed " +
                    (present.Count == 0
                        ? "no present instance"
                        : string.Join(", ", present.Select(device => device.InstanceId))) +
                    ". No device was adopted, created, or changed. Inspect the displays and approve the current candidate again.");
            }

            var adopted = new ManagedVddOwnedInstance(
                present[0].InstanceId,
                ManagedVddOwnershipKind.Adopted,
                present[0].Enabled,
                present[0].Enabled,
                null,
                now);
            var adoptedState = new ManagedVddOwnershipState(
                CurrentFormatVersion,
                revision,
                adopted,
                null);
            return new ManagedVddInstallPlan(
                ManagedVddInstallAction.UseOwnedInstance,
                adoptedState,
                adopted);
        }

        if (present.Count == 0)
        {
            var pending = new ManagedVddOwnershipState(
                CurrentFormatVersion,
                revision,
                null,
                new ManagedVddCreationIntent([], now));
            return new ManagedVddInstallPlan(
                ManagedVddInstallAction.CreateInstance,
                pending,
                null);
        }
        if (present.Count != 1)
        {
            throw Ambiguous(present.Count);
        }
        if (!acquisitionPolicy.ExactLegacyVitaOwnershipEvidence)
        {
            throw new InvalidOperationException(
                "Windows has one present ROOT\\MttVDD instance, but Vita Moonlight found neither an exact protected prior-install footprint nor explicit permission to adopt it. " +
                "The device may belong to DisplayWizard or another application, so no device or shared package was changed. " +
                "Remove the conflicting device, or explicitly choose adoption during display-driver repair after confirming its current enabled state should be restored on uninstall.");
        }

        var ownership = ManagedVddOwnershipKind.AppCreated;
        var owned = new ManagedVddOwnedInstance(
            present[0].InstanceId,
            ownership,
            null,
            present[0].Enabled,
            null,
            now);
        var ownedState = new ManagedVddOwnershipState(
            CurrentFormatVersion,
            revision,
            owned,
            null);
        return new ManagedVddInstallPlan(
            ManagedVddInstallAction.UseOwnedInstance,
            ownedState,
            owned);
    }

    internal static ManagedVddReleasePlan ClassifyRelease(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        DateTimeOffset now)
    {
        var devices = NormalizeDevices(observedDevices);
        var present = devices.Where(device => device.Present).ToArray();
        if (state is null)
        {
            if (present.Length > 0)
            {
                throw MissingOwnership();
            }
            return new ManagedVddReleasePlan(
                ManagedVddReleaseAction.Nothing,
                null,
                null,
                null);
        }

        ValidateState(state);
        if (state.ReleasedAtUtc is not null)
        {
            var released = state.Device!;
            RequireNoUnownedPresentDevices(released, devices);
            return new ManagedVddReleasePlan(
                ManagedVddReleaseAction.Nothing,
                state,
                released,
                null);
        }
        if (state.PendingCreation is not null)
        {
            if (present.Length == 0)
            {
                return new ManagedVddReleasePlan(
                    ManagedVddReleaseAction.Nothing,
                    state,
                    null,
                    null);
            }
            state = CompleteCreation(state, devices, now);
        }

        var owned = state.Device!;
        RequireNoUnownedPresentDevices(owned, devices);
        var current = present.SingleOrDefault(device =>
            InstanceIdsEqual(device.InstanceId, owned.InstanceId));
        if (current is null)
        {
            if (owned.Ownership == ManagedVddOwnershipKind.AppCreated)
            {
                return new ManagedVddReleasePlan(
                    ManagedVddReleaseAction.Nothing,
                    state,
                    owned,
                    null);
            }
            throw new InvalidOperationException(
                "The adopted managed virtual display is no longer present, so its exact prior enabled state cannot be restored. The ownership journal was retained.");
        }

        var reconciled = ReconcilePendingMutation(state, current.Enabled);
        state = reconciled.State;
        owned = state.Device!;
        if (reconciled.ConcurrentChange)
        {
            return new ManagedVddReleasePlan(
                ManagedVddReleaseAction.PreserveConcurrentChange,
                state,
                owned,
                current.Enabled);
        }
        if (owned.Ownership == ManagedVddOwnershipKind.AppCreated)
        {
            return new ManagedVddReleasePlan(
                ManagedVddReleaseAction.RemoveAppCreatedInstance,
                state,
                owned,
                false);
        }
        return new ManagedVddReleasePlan(
            ManagedVddReleaseAction.RestoreAdoptedInstance,
            state,
            owned,
            owned.PriorEnabled!.Value);
    }

    internal static ManagedVddOwnershipState BeginEnabledMutationForTest(
        ManagedVddOwnershipState state,
        bool currentEnabled,
        bool desiredEnabled)
    {
        var reconciled = ReconcilePendingMutation(state, currentEnabled);
        if (reconciled.ConcurrentChange)
        {
            throw ConcurrentChange(state.Device!.InstanceId);
        }
        state = reconciled.State;
        var owned = state.Device!;
        if (owned.LastKnownEnabled == desiredEnabled)
        {
            return state;
        }
        return Next(
            state,
            owned with
            {
                PendingMutation = new ManagedVddEnabledMutation(
                    owned.LastKnownEnabled,
                    desiredEnabled),
            });
    }

    private static IReadOnlyList<ManagedVddDeviceStatus>
        RequireOwnedPresentDevicesCore(bool required)
    {
        if (DisplayWizardAdapter.IsLegacyDriverInstalled())
        {
            throw new InvalidOperationException(
                "Windows has an unowned legacy ROOT\\IddSampleDriver virtual display. No virtual display device was changed.");
        }
        var state = LoadCore();
        var resolution = ResolveOwnedPresentDevice(
            state,
            DisplayWizardAdapter.InspectManagedDriverDevices(),
            required,
            preserveConcurrentChange: false);
        if (!Equals(state, resolution.State) && resolution.State is not null)
        {
            SaveCore(resolution.State);
        }
        return resolution.DeviceStatus is null
            ? []
            : [resolution.DeviceStatus];
    }

    private static OwnershipResolution ResolveOwnedPresentDevice(
        ManagedVddOwnershipState? state,
        IEnumerable<ManagedVddDeviceStatus> observedDevices,
        bool required,
        bool preserveConcurrentChange)
    {
        var devices = NormalizeDevices(observedDevices);
        var present = devices.Where(device => device.Present).ToArray();
        if (state is null)
        {
            if (present.Length > 0)
            {
                throw MissingOwnership();
            }
            if (!required)
            {
                return new OwnershipResolution(null, null, null, false);
            }
            throw new InvalidOperationException(
                "No journal-owned managed virtual display is present. Repair the Vita display driver before streaming.");
        }

        ValidateState(state);
        if (state.ReleasedAtUtc is not null)
        {
            if (required)
            {
                throw new InvalidOperationException(
                    "The exact managed virtual display ownership was already released during uninstall. Retry or finish uninstall before streaming.");
            }
            return new OwnershipResolution(state, null, null, false);
        }
        if (state.PendingCreation is not null)
        {
            if (!required && present.Length == 0)
            {
                return new OwnershipResolution(state, null, null, false);
            }
            throw new InvalidOperationException(
                "Managed virtual display creation did not finish. Run display-driver repair before streaming; no device was changed.");
        }
        var owned = state.Device!;
        RequireNoUnownedPresentDevices(owned, devices);
        var current = present.SingleOrDefault(device =>
            InstanceIdsEqual(device.InstanceId, owned.InstanceId));
        if (current is null)
        {
            throw new InvalidOperationException(
                $"The exact journal-owned managed virtual display {owned.InstanceId} is not present. No different device was substituted or changed.");
        }

        var reconciled = ReconcilePendingMutation(state, current.Enabled);
        if (reconciled.ConcurrentChange && !preserveConcurrentChange)
        {
            throw ConcurrentChange(owned.InstanceId);
        }
        return new OwnershipResolution(
            reconciled.State,
            reconciled.State.Device,
            current,
            reconciled.ConcurrentChange);
    }

    private static MutationReconciliation ReconcilePendingMutation(
        ManagedVddOwnershipState state,
        bool currentEnabled)
    {
        ValidateState(state);
        var owned = state.Device!;
        if (owned.PendingMutation is { } pending)
        {
            if (currentEnabled == pending.DesiredEnabled)
            {
                return new MutationReconciliation(
                    Next(
                        state,
                        owned with
                        {
                            LastKnownEnabled = currentEnabled,
                            PendingMutation = null,
                        }),
                    false);
            }
            if (currentEnabled == pending.ExpectedEnabled)
            {
                return new MutationReconciliation(
                    Next(state, owned with { PendingMutation = null }),
                    false);
            }
            throw new InvalidDataException(
                "The managed-VDD enabled-state intent is internally inconsistent.");
        }
        return new MutationReconciliation(
            state,
            currentEnabled != owned.LastKnownEnabled);
    }

    private static void RequireNoUnownedPresentDevices(
        ManagedVddOwnedInstance owned,
        IReadOnlyList<ManagedVddDeviceStatus> devices)
    {
        var unowned = devices
            .Where(device =>
                device.Present &&
                !InstanceIdsEqual(device.InstanceId, owned.InstanceId))
            .Select(device => device.InstanceId)
            .ToArray();
        if (unowned.Length > 0)
        {
            throw new InvalidOperationException(
                "Windows exposed one or more present ROOT\\MttVDD nodes that are not owned by Vita Moonlight. " +
                "No ambiguous or unrelated device was changed: " +
                string.Join(", ", unowned));
        }
    }

    private static IReadOnlyList<ManagedVddDeviceStatus> NormalizeDevices(
        IEnumerable<ManagedVddDeviceStatus> observedDevices)
    {
        ArgumentNullException.ThrowIfNull(observedDevices);
        var devices = observedDevices.ToArray();
        if (devices.Any(device => !IsValidInstanceId(device.InstanceId)) ||
            devices.Select(device => device.InstanceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != devices.Length)
        {
            throw new InvalidDataException(
                "Windows returned an invalid or duplicate managed-VDD device identity.");
        }
        return devices;
    }

    private static ManagedVddOwnershipState? LoadCore()
    {
        if (!File.Exists(JournalFile)) return null;
        var information = new FileInfo(JournalFile);
        if (information.Length <= 0 ||
            information.Length > MaximumJournalBytes)
        {
            throw new InvalidDataException(
                "The managed-VDD ownership journal has an invalid size.");
        }
        var state = JsonSerializer.Deserialize<ManagedVddOwnershipState>(
            TrustedFileSystem.ReadAllText(JournalFile),
            JsonOptions)
            ?? throw new InvalidDataException(
                "The managed-VDD ownership journal is empty.");
        ValidateState(state);
        return state;
    }

    private static void SaveCore(ManagedVddOwnershipState state)
    {
        ValidateState(state);
        TrustedFileSystem.WriteAllText(
            JournalFile,
            JsonSerializer.Serialize(state, JsonOptions));
    }

    private static void SecureLocked(DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        MachineStateSecurity.SecureWhileDisplayTransactionHeld(transaction);
    }

    private static void SecureMigrationLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        if (MachineStateSecurity.IsProtectionInitialized())
        {
            MachineStateSecurity.SecureWhileDisplayTransactionHeld(
                transaction);
            return;
        }
        // The recovery-upgrade display lease is acquired through the typed
        // installer/bootstrap gate. Secure the fixed installed state root
        // before reading evidence or publishing the first journal; the caller
        // marks the full migration initialized only after physical recovery.
        MachineStateSecurity.SecureContainer();
        TrustedFileSystem.SecureExistingFile(JournalFile);
    }

    private static void ValidateState(ManagedVddOwnershipState state)
    {
        if (state.FormatVersion != CurrentFormatVersion ||
            state.Revision <= 0 ||
            (state.Device is null) == (state.PendingCreation is null) ||
            (state.ReleasedAtUtc is null) != (state.ReleasedBy is null) ||
            state.ReleasedAtUtc is not null && state.Device is null ||
            state.ReleasedBy is { } releasedBy &&
                !Enum.IsDefined(releasedBy))
        {
            throw new InvalidDataException(
                "The managed-VDD ownership journal has an unsupported structure.");
        }
        if (state.PendingCreation is { } creation)
        {
            if (creation.PresentInstanceIdsBeforeCreate is null ||
                creation.PresentInstanceIdsBeforeCreate.Any(
                    instanceId => !IsValidInstanceId(instanceId)) ||
                creation.PresentInstanceIdsBeforeCreate.Count !=
                    creation.PresentInstanceIdsBeforeCreate
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count())
            {
                throw new InvalidDataException(
                    "The managed-VDD creation intent contains an invalid device identity.");
            }
            return;
        }

        var owned = state.Device!;
        if (!IsValidInstanceId(owned.InstanceId) ||
            !Enum.IsDefined(owned.Ownership) ||
            owned.Ownership == ManagedVddOwnershipKind.AppCreated &&
                owned.PriorEnabled is not null ||
            owned.Ownership == ManagedVddOwnershipKind.Adopted &&
                owned.PriorEnabled is null ||
            state.ReleasedAtUtc is not null &&
                owned.PendingMutation is not null ||
            owned.PendingMutation is { } pending &&
                (pending.ExpectedEnabled != owned.LastKnownEnabled ||
                 pending.ExpectedEnabled == pending.DesiredEnabled))
        {
            throw new InvalidDataException(
                "The managed-VDD ownership journal contains an invalid device record.");
        }
    }

    private static ManagedVddOwnershipState Next(
        ManagedVddOwnershipState state,
        ManagedVddOwnedInstance owned) =>
        state with
        {
            Revision = checked(state.Revision + 1),
            Device = owned,
            PendingCreation = null,
            ReleasedAtUtc = null,
            ReleasedBy = null,
        };

    internal static bool IsValidInstanceIdForAdoption(string? instanceId) =>
        IsValidInstanceId(instanceId);

    private static bool IsValidInstanceId(string? instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        instanceId.Length <= 512 &&
        instanceId.StartsWith(
            @"ROOT\DISPLAY\",
            StringComparison.OrdinalIgnoreCase) &&
        !instanceId.Any(char.IsControl);

    private static bool InstanceIdsEqual(string first, string second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException MissingOwnership() =>
        new(
            "Windows has a present ROOT\\MttVDD device, but Vita Moonlight has no exact ownership journal for it. " +
            "Run explicit display-driver repair to adopt one unambiguous instance; no device was changed.");

    private static InvalidOperationException Ambiguous(int count) =>
        new(
            "Vita Moonlight cannot safely adopt a managed virtual display because Windows did not expose exactly one present ROOT\\MttVDD instance. " +
            $"Observed present instances: {count}. No ambiguous or unrelated device was changed.");

    private static InvalidOperationException ConcurrentChange(
        string instanceId) =>
        new(
            $"The enabled state of journal-owned managed virtual display {instanceId} changed outside Vita Moonlight. " +
            "The concurrent change was preserved and no device was changed. Run explicit display-driver repair only after resolving the other controller.");

    private sealed record OwnershipResolution(
        ManagedVddOwnershipState? State,
        ManagedVddOwnedInstance? Device,
        ManagedVddDeviceStatus? DeviceStatus,
        bool ConcurrentChange);

    private sealed record MutationReconciliation(
        ManagedVddOwnershipState State,
        bool ConcurrentChange);
}
