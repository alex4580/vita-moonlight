using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VitaMoonlight.Host;

internal sealed record SunshineConfigurationResult(
    string ConfigurationDirectory,
    string ApplicationName,
    int CoveredApplicationCount,
    bool UsesNativeDisplayManagement,
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

        var useNativeDisplayManagement = settings.HostMode == "sunshine" && settings.IntegrateAllSunshineApps;
        var targets = useNativeDisplayManagement ? Array.Empty<JsonObject>() : new[] { managedApp };
        foreach (var app in targets)
        {
            AddManagedHook(app, companionPath);
        }

        var configurationLines = File.Exists(sunshineConfigPath)
            ? File.ReadAllLines(sunshineConfigPath).ToList()
            : new List<string>();
        UpdateSunshineConfiguration(configurationLines);
        if (useNativeDisplayManagement)
        {
            var displayDeviceId = FindManagedDisplayDeviceId(
                Path.Combine(configDirectory, "sunshine.log"),
                settings.DisplayMatch);
            if (displayDeviceId is null)
            {
                throw new InvalidOperationException(
                    "Sunshine has not enumerated the Vita virtual display yet. Restart Sunshine once, then apply configuration again.");
            }
            ConfigureNativeDisplayManagement(configurationLines, displayDeviceId, settings.ForceSdr);
        }
        else if (settings.HostMode == "sunshine")
        {
            SetConfigurationValue(configurationLines, "dd_configuration_option", "disabled");
            SetConfigurationValue(configurationLines, "dd_config_revert_on_disconnect", "disabled");
        }
        DisplayTopologyService.AtomicWrite(appsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        DisplayTopologyService.AtomicWrite(
            sunshineConfigPath,
            string.Join(Environment.NewLine, configurationLines) + Environment.NewLine);
        return new SunshineConfigurationResult(
            configDirectory,
            settings.SunshineApplicationName,
            useNativeDisplayManagement ? applicationObjects.Length : targets.Length,
            useNativeDisplayManagement,
            backupPath);
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

    internal static void ConfigureNativeDisplayManagement(List<string> lines, string displayDeviceId, bool forceSdr)
    {
        SetConfigurationValue(lines, "output_name", displayDeviceId);
        SetConfigurationValue(lines, "dd_configuration_option", "ensure_only_display");
        SetConfigurationValue(lines, "dd_resolution_option", "auto");
        SetConfigurationValue(lines, "dd_refresh_rate_option", "auto");
        SetConfigurationValue(lines, "dd_hdr_option", forceSdr ? "auto" : "disabled");
        SetConfigurationValue(lines, "dd_config_revert_delay", "500");
        SetConfigurationValue(lines, "dd_config_revert_on_disconnect", "enabled");
    }

    internal static string? FindManagedDisplayDeviceId(string logPath, string? displayMatch)
    {
        if (!File.Exists(logPath)) return null;
        string log;
        try
        {
            using var stream = new FileStream(
                logPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            log = reader.ReadToEnd();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var matches = Regex.Matches(
            log,
            "\\\"device_id\\\"\\s*:\\s*\\\"(?<id>\\{[^\\\"]+\\})\\\"(?:(?!\\\"device_id\\\").)*?\\\"friendly_name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]*)\\\"",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var id = matches[index].Groups["id"].Value;
            var name = matches[index].Groups["name"].Value;
            var identity = $"{name} {id}";
            if (!string.IsNullOrWhiteSpace(displayMatch)
                ? identity.Contains(displayMatch, StringComparison.OrdinalIgnoreCase)
                : identity.Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase) ||
                  identity.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase) ||
                  identity.Contains("IddSample", StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }
        }
        return null;
    }

    internal static bool IsNativeDisplayManagementReady(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "sunshine.conf");
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path);
            return HasConfigurationValue(lines, "dd_configuration_option", "ensure_only_display") &&
                   HasConfigurationValue(lines, "dd_config_revert_on_disconnect", "enabled") &&
                   lines.Any(line => line.TrimStart().StartsWith("output_name =", StringComparison.OrdinalIgnoreCase) &&
                                     !string.IsNullOrWhiteSpace(line[(line.IndexOf('=') + 1)..]));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasConfigurationValue(IEnumerable<string> lines, string key, string value) =>
        lines.Any(line => string.Equals(line.Trim(), $"{key} = {value}", StringComparison.OrdinalIgnoreCase));

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
