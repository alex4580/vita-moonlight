using System.Text.Json;
using System.Text.Json.Nodes;

namespace VitaMoonlight.Host;

internal sealed record SunshineConfigurationResult(
    string ConfigurationDirectory,
    string ApplicationName,
    int HookedApplicationCount,
    string BackupPath);

internal static class SunshineConfigurator
{
    private const string HookMarker = "VitaMoonlight.Host";

    internal static SunshineConfigurationResult Configure(HostSettings settings, string companionPath)
    {
        var configDirectory = ResolveConfigurationDirectory(settings.SunshineConfigDirectory, settings.HostMode);
        Directory.CreateDirectory(configDirectory);
        var appsPath = Path.Combine(configDirectory, "apps.json");
        var sunshineConfigPath = Path.Combine(configDirectory, "sunshine.conf");
        var backupPath = BackupOnce(appsPath);

        var root = File.Exists(appsPath)
            ? JsonNode.Parse(File.ReadAllText(appsPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        var apps = root["apps"] as JsonArray ?? new JsonArray();
        root["apps"] = apps;

        var managedApp = apps.OfType<JsonObject>().FirstOrDefault(candidate =>
            string.Equals(candidate["name"]?.GetValue<string>(), settings.SunshineApplicationName, StringComparison.OrdinalIgnoreCase));
        if (managedApp is null)
        {
            managedApp = new JsonObject
            {
                ["name"] = settings.SunshineApplicationName,
                ["cmd"] = string.Empty,
                ["output"] = string.Empty,
                ["exclude-global-prep-cmd"] = false,
            };
            apps.Add(managedApp);
        }

        var applicationObjects = apps.OfType<JsonObject>().ToArray();
        foreach (var app in applicationObjects)
        {
            RemoveManagedHooks(app);
        }

        var targets = settings.IntegrateAllSunshineApps ? applicationObjects : new[] { managedApp };
        foreach (var app in targets)
        {
            AddManagedHook(app, companionPath);
        }

        DisplayTopologyService.AtomicWrite(appsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var configurationLines = File.Exists(sunshineConfigPath)
            ? File.ReadAllLines(sunshineConfigPath).ToList()
            : new List<string>();
        UpdateSunshineConfiguration(configurationLines);
        DisplayTopologyService.AtomicWrite(
            sunshineConfigPath,
            string.Join(Environment.NewLine, configurationLines) + Environment.NewLine);
        return new SunshineConfigurationResult(configDirectory, settings.SunshineApplicationName, targets.Length, backupPath);
    }

    private static void RemoveManagedHooks(JsonObject app)
    {
        if (app["prep-cmd"] is not JsonArray prepCommands) return;
        for (var index = prepCommands.Count - 1; index >= 0; index--)
        {
            if (prepCommands[index] is JsonObject existing &&
                (existing["do"]?.ToString().Contains(HookMarker, StringComparison.OrdinalIgnoreCase) == true ||
                 existing["undo"]?.ToString().Contains(HookMarker, StringComparison.OrdinalIgnoreCase) == true))
            {
                prepCommands.RemoveAt(index);
            }
        }
    }

    private static void AddManagedHook(JsonObject app, string companionPath)
    {
        var prepCommands = app["prep-cmd"] as JsonArray ?? new JsonArray();
        app["prep-cmd"] = prepCommands;
        prepCommands.Add(new JsonObject
        {
            ["do"] = BuildStartCommand(companionPath),
            ["undo"] = BuildStopCommand(companionPath),
            ["elevated"] = true,
        });
    }

    internal static string BuildStartCommand(string companionPath)
    {
        ValidateExecutablePath(companionPath);
        return $"cmd.exe /D /S /C \"\"{companionPath}\" session start --width %SUNSHINE_CLIENT_WIDTH% --height %SUNSHINE_CLIENT_HEIGHT% --fps %SUNSHINE_CLIENT_FPS%\"";
    }

    internal static string BuildStopCommand(string companionPath)
    {
        ValidateExecutablePath(companionPath);
        return $"\"{companionPath}\" session stop";
    }

    internal static string ResolveConfigurationDirectory(string? configuredDirectory, string hostMode)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            return Path.GetFullPath(configuredDirectory);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var folder = hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase) ? "Apollo" : "Sunshine";
        return Path.Combine(programFiles, folder, "config");
    }

    internal static void UpdateSunshineConfiguration(List<string> lines)
    {
        SetConfigurationValue(lines, "controller", "enabled");
        SetConfigurationValue(lines, "gamepad", "auto");
        SetConfigurationValue(lines, "motion_as_ds4", "enabled");
        SetConfigurationValue(lines, "touchpad_as_ds4", "enabled");
        SetConfigurationValue(lines, "keyboard", "enabled");
        SetConfigurationValue(lines, "mouse", "enabled");
        SetConfigurationValue(lines, "native_pen_touch", "enabled");
    }

    private static void SetConfigurationValue(List<string> lines, string key, string value)
    {
        var prefix = key + " =";
        var index = lines.FindIndex(line => line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var replacement = $"{key} = {value}";
        if (index >= 0)
        {
            lines[index] = replacement;
        }
        else
        {
            lines.Add(replacement);
        }
    }

    private static string BackupOnce(string path)
    {
        var backupPath = path + ".vita-moonlight.backup";
        if (File.Exists(path) && !File.Exists(backupPath))
        {
            File.Copy(path, backupPath);
        }
        return backupPath;
    }

    private static void ValidateExecutablePath(string companionPath)
    {
        if (string.IsNullOrWhiteSpace(companionPath) || companionPath.Contains('"'))
        {
            throw new ArgumentException("The companion executable path is invalid.", nameof(companionPath));
        }
    }
}
