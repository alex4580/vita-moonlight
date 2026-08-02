using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DeferredHostSetupPlan(
    int FormatVersion,
    string HostMode,
    bool InstallVirtualDisplay,
    DateTimeOffset RequestedAtUtc);

/// <summary>
/// Records installer work which cannot be performed while Vita host features
/// are deliberately paused. The plan contains choices only; it never grants
/// authority to an executable path supplied by a user. Completion always uses
/// the protected installers bundled beneath the installed application.
/// </summary>
internal static class DeferredHostSetupStore
{
    private const int CurrentFormatVersion = 1;
    private const int MaximumPlanBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string PlanFile => Path.Combine(
        HostStatePaths.Root,
        "deferred-host-setup.json");

    internal static DeferredHostSetupPlan? Load()
    {
        if (!File.Exists(PlanFile)) return null;
        var information = new FileInfo(PlanFile);
        if (information.Length <= 0 || information.Length > MaximumPlanBytes)
        {
            throw new InvalidDataException(
                "The deferred host-setup plan has an invalid size.");
        }

        var plan = JsonSerializer.Deserialize<DeferredHostSetupPlan>(
            TrustedFileSystem.ReadAllText(PlanFile),
            JsonOptions)
            ?? throw new InvalidDataException(
                "The deferred host-setup plan is empty.");
        Validate(plan);
        return plan;
    }

    internal static void Save(string hostMode, bool installVirtualDisplay)
    {
        using var operationLock = BackendLifecycleStateStore.AcquireLock();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        SaveLocked(operationLock, hostMode, installVirtualDisplay);
    }

    internal static void SaveLocked(
        BackendOperationLease operation,
        string hostMode,
        bool installVirtualDisplay)
    {
        operation.RequireActive();
        var plan = new DeferredHostSetupPlan(
            CurrentFormatVersion,
            NormalizeHostMode(hostMode),
            installVirtualDisplay,
            DateTimeOffset.UtcNow);
        Validate(plan);
        MachineStateSecurity.Secure();
        TrustedFileSystem.WriteAllText(
            PlanFile,
            JsonSerializer.Serialize(plan, JsonOptions));
    }

    internal static void Delete()
    {
        using var operationLock = BackendLifecycleStateStore.AcquireLock();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        DeleteLocked(operationLock);
    }

    internal static void DeleteLocked(BackendOperationLease operation)
    {
        operation.RequireActive();
        MachineStateSecurity.Secure();
        TrustedFileSystem.DeleteFile(PlanFile);
    }

    private static void Validate(DeferredHostSetupPlan plan)
    {
        if (plan.FormatVersion != CurrentFormatVersion ||
            !string.Equals(
                plan.HostMode,
                NormalizeHostMode(plan.HostMode),
                StringComparison.Ordinal) ||
            plan.InstallVirtualDisplay && plan.HostMode != "sunshine")
        {
            throw new InvalidDataException(
                "The deferred host-setup plan contains an unsupported value.");
        }
    }

    private static string NormalizeHostMode(string? hostMode)
    {
        var normalized = hostMode?.Trim().ToLowerInvariant();
        return normalized is "sunshine" or "apollo"
            ? normalized
            : throw new InvalidDataException(
                "Deferred host setup must select Sunshine or Apollo.");
    }
}
