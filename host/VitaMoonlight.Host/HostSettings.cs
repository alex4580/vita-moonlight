using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record HostSettings(
    string HostMode,
    string? SunshineConfigDirectory,
    string SunshineApplicationName,
    string? DisplayWizardPath,
    string? DisplayMatch)
{
    internal static HostSettings Default { get; } = new("sunshine", null, "Vita Moonlight", null, null);

    internal static HostSettings Load()
    {
        if (!File.Exists(HostStatePaths.SettingsFile))
        {
            return Default;
        }
        return JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(HostStatePaths.SettingsFile), JsonOptions)
            ?? Default;
    }

    internal void Save()
    {
        DisplayTopologyService.AtomicWrite(HostStatePaths.SettingsFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
