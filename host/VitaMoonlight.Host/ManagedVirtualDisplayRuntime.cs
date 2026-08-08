namespace VitaMoonlight.Host;

internal sealed record ManagedVirtualDisplayIdleResult(
    IReadOnlyList<string> PhysicalDisplays,
    IReadOnlyList<string> DisabledInstanceIds);

/// <summary>
/// Owns the runtime invariant for the Vita display. Installing the driver and
/// enabling Vita host features must not leave an available fallback monitor
/// behind: outside an active, journaled stream transaction, Windows must have
/// a physical-only topology and every managed MTT device node must be stopped.
/// </summary>
internal static class ManagedVirtualDisplayRuntime
{
    internal static ManagedVirtualDisplayIdleResult ReconcileIdleLocked(
        DisplayTransactionLease transaction,
        bool requireManagedDevice = false)
    {
        transaction.RequireActive();
        var topology = new DisplayTopologyService();
        // Hardware IDs identify a driver family, not ownership. Only the
        // exact device instance durably acquired by this installation may be
        // toggled. Optional reconciliation simply has no PnP target when no
        // owned instance exists; required stream/repair paths fail closed.
        var presentInstanceIds = ManagedVddOwnershipJournal
            .RequireOwnedPresentDevicesLocked(
                transaction,
                required: requireManagedDevice)
            .Select(device => device.InstanceId)
            .ToArray();

        if (presentInstanceIds.Length == 0)
        {
            // No exact instance authority means no broad topology authority
            // either. In particular, a released adopted-device tombstone must
            // not let final cleanup deactivate the external display whose
            // original state was just restored. Accept an already-safe exact
            // physical layout, otherwise fail without changing any display.
            if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                    out var untouched) ||
                untouched is null)
            {
                throw new InvalidOperationException(
                    "Windows did not expose an exact physical-only idle layout, and Vita Moonlight owns no virtual-display instance it may safely change.");
            }
            return new ManagedVirtualDisplayIdleResult(
                untouched.PhysicalDisplays,
                []);
        }

        PhysicalOnlyDisplaySnapshot? exactBaseline = null;
        Exception? preparationFailure = null;
        try
        {
            if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                    out exactBaseline))
            {
                // This is the emergency path for a contaminated/VDD-only
                // topology. It may synthesize a physical layout because no
                // exact physical-only layout is currently available to keep.
                _ = topology.RecoverPhysicalDisplays();
                topology.DisableManagedVirtualDisplays();
                UninstallManager.VerifyPhysicalOnlyTopology(topology);
                if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                        out exactBaseline))
                {
                    throw new InvalidOperationException(
                        "Windows did not expose a complete physical-only display snapshot after emergency recovery.");
                }
            }
        }
        catch (Exception error)
        {
            preparationFailure = error;
        }

        Exception? disableFailure = null;
        try
        {
            if (presentInstanceIds.Length > 0)
            {
                // Idle safety is intentionally stronger than the generic
                // device-control guard. A sleeping/disconnected physical
                // monitor must not leave the managed VDD available as the
                // fallback desktop. Only this transaction-owned runtime path
                // may bypass the pre-disable physical-topology proof.
                DisplayWizardAdapter.SetManagedDriverEnabled(
                    transaction,
                    presentInstanceIds,
                    enabled: false,
                    requirePhysicalOnlyBeforeDisable: false);
            }
        }
        catch (Exception error)
        {
            disableFailure = error;
        }

        IReadOnlyList<string>? physicalDisplays = null;
        Exception? finalTopologyFailure = null;
        try
        {
            if (exactBaseline is not null)
            {
                topology.RestoreExactPhysicalOnlySnapshot(exactBaseline);
                physicalDisplays = exactBaseline.PhysicalDisplays;
            }
            else
            {
                physicalDisplays = topology.RecoverPhysicalDisplays();
                topology.DisableManagedVirtualDisplays();
                UninstallManager.VerifyPhysicalOnlyTopology(topology);
            }
        }
        catch (Exception error)
        {
            finalTopologyFailure = error;
        }

        ThrowIfIdleReconciliationFailed(
            "The managed Vita display could not enter its PnP-disabled idle state.",
            disableFailure,
            finalTopologyFailure,
            finalTopologyFailure is null ? null : preparationFailure);
        return new ManagedVirtualDisplayIdleResult(
            physicalDisplays!,
            presentInstanceIds);
    }

    /// <summary>
    /// Enters idle after an exact, physical-only recovery snapshot has already
    /// been captured. Reapplying that snapshot after the PnP mutation preserves
    /// monitor positions and primary-display choice; the generic emergency
    /// recovery path is intentionally not used because Windows may otherwise
    /// synthesize a new extended layout and move application windows.
    /// </summary>
    internal static ManagedVirtualDisplayIdleResult
        ReconcileRestoredPhysicalBaselineLocked(
            DisplayTransactionLease transaction,
            Action restorePhysicalBaseline,
            bool requireManagedDevice = false)
    {
        transaction.RequireActive();
        ArgumentNullException.ThrowIfNull(restorePhysicalBaseline);
        var topology = new DisplayTopologyService();
        var presentInstanceIds = ManagedVddOwnershipJournal
            .RequireOwnedPresentDevicesLocked(
                transaction,
                required: requireManagedDevice)
            .Select(device => device.InstanceId)
            .ToArray();

        if (presentInstanceIds.Length == 0)
        {
            // A released/adopted device is no longer ours to deactivate.
            // Reapply only the caller-owned exact recovery transaction and
            // prove the result; never run broad virtual-display cleanup.
            restorePhysicalBaseline();
            UninstallManager.VerifyPhysicalOnlyTopology(topology);
            if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                    out var restored) ||
                restored is null)
            {
                throw new InvalidOperationException(
                    "The saved recovery record did not produce a complete physical-only display snapshot.");
            }
            return new ManagedVirtualDisplayIdleResult(
                restored.PhysicalDisplays,
                []);
        }

        PhysicalOnlyDisplaySnapshot? exactBaseline = null;
        Exception? firstRestoreFailure = null;
        try
        {
            restorePhysicalBaseline();
            UninstallManager.VerifyPhysicalOnlyTopology(topology);
            if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                    out exactBaseline))
            {
                throw new InvalidOperationException(
                    "The saved recovery record did not produce a complete physical-only display snapshot.");
            }
        }
        catch (Exception error)
        {
            firstRestoreFailure = error;
        }

        Exception? disableFailure = null;
        try
        {
            if (presentInstanceIds.Length > 0)
            {
                DisplayWizardAdapter.SetManagedDriverEnabled(
                    transaction,
                    presentInstanceIds,
                    enabled: false,
                    requirePhysicalOnlyBeforeDisable: false);
            }
        }
        catch (Exception error)
        {
            disableFailure = error;
        }

        IReadOnlyList<string>? physicalDisplays = null;
        Exception? finalRestoreFailure = null;
        try
        {
            if (exactBaseline is not null)
            {
                topology.RestoreExactPhysicalOnlySnapshot(exactBaseline);
                physicalDisplays = exactBaseline.PhysicalDisplays;
            }
            else
            {
                // The monitor may have become available after the first
                // attempt. Re-run the exact recovery delegate only after the
                // fallback VDD has been stopped, then validate what it applied.
                restorePhysicalBaseline();
                UninstallManager.VerifyPhysicalOnlyTopology(topology);
                if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                        out var recovered) || recovered is null)
                {
                    throw new InvalidOperationException(
                        "The saved recovery record did not produce a complete physical-only display snapshot.");
                }
                topology.RestoreExactPhysicalOnlySnapshot(recovered);
                physicalDisplays = recovered.PhysicalDisplays;
            }
        }
        catch (Exception error)
        {
            finalRestoreFailure = error;
        }

        ThrowIfIdleReconciliationFailed(
            "The exact physical display baseline could not be restored with the managed Vita display stopped.",
            disableFailure,
            finalRestoreFailure,
            finalRestoreFailure is null ? null : firstRestoreFailure);
        return new ManagedVirtualDisplayIdleResult(
            physicalDisplays!,
            presentInstanceIds);
    }

    private static void ThrowIfIdleReconciliationFailed(
        string message,
        params Exception?[] possibleFailures)
    {
        var failures = possibleFailures
            .Where(error => error is not null)
            .Cast<Exception>()
            .Distinct()
            .ToArray();
        if (failures.Length == 0) return;
        throw new InvalidOperationException(
            message,
            failures.Length == 1
                ? failures[0]
                : new AggregateException(failures));
    }
}
