using System.Reflection;
using System.Text;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record SupportDisplayReport(
    int Number,
    string Name,
    bool Active,
    bool Available,
    bool ManagedVitaDisplay,
    bool OtherVirtualDisplay,
    int OutputTechnology);

internal sealed record SupportReport(
    int SchemaVersion,
    string ReportId,
    DateTimeOffset GeneratedAtUtc,
    string HostVersion,
    bool InstalledPackage,
    string OperatingSystem,
    string Architecture,
    bool SupportedPlatform,
    bool Administrator,
    string HostMode,
    bool SunshineInstalled,
    string? SunshineVersion,
    bool SunshineVersionSupported,
    bool ApolloInstalled,
    bool ViGEmBusInstalled,
    bool ViGEmBusRunning,
    bool SunshineNeedsRestart,
    bool VisualCppRuntimeSupported,
    string? VisualCppRuntimeVersion,
    bool VirtualDisplayDriverInstalled,
    bool VirtualDisplayModesReady,
    bool NativeDisplayLifecycleReady,
    bool IntegrateAllSunshineApps,
    bool ForceSdr,
    bool RecoveryPending,
    bool RecoveryTaskInstalled,
    bool RescueAgentInstalled,
    bool RescueAgentRunning,
    IReadOnlyList<HostModeHotkeyStatus> ModeHotkeys,
    IReadOnlyList<SupportDisplayReport> Displays,
    string? DisplayInventoryError,
    string Recommendation);

internal static class SupportReportExporter
{
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static string DefaultFileName() =>
        $"Vita-Moonlight-Host-Support-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";

    internal static string Export(string outputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var fullPath = Path.GetFullPath(outputPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The support report needs a destination directory.", nameof(outputPath));
        }

        Directory.CreateDirectory(directory);
        var report = Create();
        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    internal static SupportReport Create()
    {
        var diagnostics = HostDiagnostics.Inspect();
        var displays = Array.Empty<SupportDisplayReport>();
        string? displayInventoryError = null;
        try
        {
            displays = new DisplayTopologyService()
                .ListDisplays()
                .Select((display, index) =>
                {
                    var managed = DisplayTopologyService.IsManagedVirtualDisplay(display);
                    var otherVirtual =
                        DisplayTopologyService.IsLikelyVirtualDisplay(display) && !managed;
                    var kind = managed
                        ? "Vita virtual display"
                        : otherVirtual
                            ? "Other virtual display"
                            : "Physical display";
                    return new SupportDisplayReport(
                        index + 1,
                        kind,
                        display.IsActive,
                        display.IsAvailable,
                        managed,
                        otherVirtual,
                        display.OutputTechnology);
                })
                .ToArray();
        }
        catch (Exception error)
        {
            // Keep the report useful without copying exception messages that
            // may contain machine-specific paths or device identifiers.
            displayInventoryError = $"{error.GetType().Name}:0x{error.HResult:X8}";
        }

        return Create(
            diagnostics,
            displays,
            displayInventoryError,
            InstallationTrust.IsInstalledPayload(out _),
            ProductVersion());
    }

    internal static SupportReport Create(
        HostDiagnosticReport diagnostics,
        IReadOnlyList<SupportDisplayReport> displays,
        string? displayInventoryError,
        bool installedPackage,
        string hostVersion) =>
        new(
            CurrentSchemaVersion,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow,
            hostVersion,
            installedPackage,
            diagnostics.OperatingSystem,
            diagnostics.Architecture,
            diagnostics.IsSupportedPlatform,
            diagnostics.IsAdministrator,
            NormalizeHostMode(diagnostics.HostMode),
            diagnostics.SunshinePath is not null,
            diagnostics.SunshineVersion,
            diagnostics.SunshineVersionSupported,
            diagnostics.ApolloPath is not null,
            diagnostics.ViGEmBusInstalled,
            diagnostics.ViGEmBusRunning,
            diagnostics.SunshineNeedsRestart,
            diagnostics.VisualCppRuntimeSupported,
            diagnostics.VisualCppRuntimeVersion,
            diagnostics.VirtualDisplayDriverInstalled,
            diagnostics.VirtualDisplayModesReady,
            diagnostics.NativeDisplayLifecycleReady,
            diagnostics.IntegrateAllSunshineApps,
            diagnostics.ForceSdr,
            diagnostics.RecoveryPending,
            diagnostics.RecoveryTaskInstalled,
            diagnostics.RescueAgentInstalled,
            diagnostics.RescueAgentRunning,
            diagnostics.ModeHotkeys,
            NormalizeDisplays(displays),
            displayInventoryError,
            diagnostics.Recommendation);

    internal static string Serialize(SupportReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    private static string ProductVersion() =>
        typeof(SupportReportExporter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(SupportReportExporter).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string NormalizeHostMode(string? hostMode) =>
        hostMode?.Trim().ToLowerInvariant() switch
        {
            "sunshine" => "sunshine",
            "apollo" => "apollo",
            _ => "unknown",
        };

    private static IReadOnlyList<SupportDisplayReport> NormalizeDisplays(
        IReadOnlyList<SupportDisplayReport> displays) =>
        displays.Select(display => display with
        {
            Name = display.ManagedVitaDisplay
                ? "Vita virtual display"
                : display.OtherVirtualDisplay
                    ? "Other virtual display"
                    : "Physical display",
        }).ToArray();

}
