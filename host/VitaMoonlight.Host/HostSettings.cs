using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record HostSettings(
    string HostMode,
    string? SunshineConfigDirectory,
    string SunshineApplicationName,
    string? DisplayWizardPath,
    string? DisplayMatch,
    int FormatVersion,
    bool IntegrateAllSunshineApps,
    bool ForceSdr)
{
    private const int CurrentFormatVersion = 2;

    internal static HostSettings Default { get; } = new(
        "sunshine",
        null,
        "Vita Moonlight",
        null,
        null,
        CurrentFormatVersion,
        true,
        true);

    internal static HostSettings Load()
    {
        if (!File.Exists(HostStatePaths.SettingsFile))
        {
            return Default;
        }
        var loaded = JsonSerializer.Deserialize<HostSettings>(
            TrustedFileSystem.ReadAllText(HostStatePaths.SettingsFile),
            JsonOptions)
            ?? Default;
        return loaded.FormatVersion < CurrentFormatVersion
            ? loaded with
            {
                FormatVersion = CurrentFormatVersion,
                IntegrateAllSunshineApps = true,
                ForceSdr = true,
            }
            : loaded;
    }

    internal void Save()
    {
        var current = this with { FormatVersion = CurrentFormatVersion };
        MachineStateSecurity.Secure();
        TrustedFileSystem.WriteAllText(
            HostStatePaths.SettingsFile,
            JsonSerializer.Serialize(current, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
