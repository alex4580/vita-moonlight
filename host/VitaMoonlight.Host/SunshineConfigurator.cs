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
    private const string VitaDisplayModeRemapping =
        "{\"mixed\":[],\"resolution_only\":[" +
        "{\"requested_resolution\":\"960x540\",\"final_resolution\":\"960x540\"}," +
        "{\"requested_resolution\":\"960x544\",\"final_resolution\":\"960x544\"}," +
        "{\"requested_resolution\":\"1280x720\",\"final_resolution\":\"1280x720\"}," +
        "{\"final_resolution\":\"960x540\"}],\"refresh_rate_only\":[]}";

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
        return StreamingHostLocator.ResolveConfigurationDirectory(configuredDirectory, hostMode);
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
        // Keep the Windows desktop at a driver-safe 60 Hz. The client encoder
        // can still stream at 24/30/40/50/60 FPS independently.
        SetConfigurationValue(lines, "dd_refresh_rate_option", "manual");
        SetConfigurationValue(lines, "dd_manual_refresh_rate", "60");
        SetConfigurationValue(lines, "dd_mode_remapping", VitaDisplayModeRemapping);
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

        var candidates = ParseDisplayCandidates(log);
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index];
            var identity = $"{candidate.Name} {candidate.Id} {candidate.Raw}";
            if (!string.IsNullOrWhiteSpace(displayMatch)
                ? identity.Contains(displayMatch, StringComparison.OrdinalIgnoreCase)
                : IsManagedDisplayIdentity(identity))
            {
                return candidate.Id;
            }
        }
        return null;
    }

    private static IReadOnlyList<SunshineDisplayCandidate> ParseDisplayCandidates(string log)
    {
        var candidates = new List<SunshineDisplayCandidate>();
        const string marker = "Currently available display devices:";
        for (var markerIndex = log.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
             markerIndex >= 0;
             markerIndex = log.IndexOf(marker, markerIndex + marker.Length, StringComparison.OrdinalIgnoreCase))
        {
            var arrayStart = log.IndexOf('[', markerIndex + marker.Length);
            if (arrayStart < 0) continue;
            var arrayEnd = FindJsonArrayEnd(log, arrayStart);
            if (arrayEnd < 0) continue;
            try
            {
                using var document = JsonDocument.Parse(log[arrayStart..(arrayEnd + 1)]);
                foreach (var display in document.RootElement.EnumerateArray())
                {
                    if (!display.TryGetProperty("device_id", out var idElement)) continue;
                    var id = idElement.GetString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    var name = display.TryGetProperty("friendly_name", out var nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;
                    candidates.Add(new SunshineDisplayCandidate(id, name, display.GetRawText()));
                }
            }
            catch (JsonException)
            {
                // Older or development Sunshine builds may emit non-strict JSON; regex fallback below handles those.
            }
        }
        if (candidates.Count > 0) return candidates;

        var idFirst = Regex.Matches(
            log,
            "\\\"device_id\\\"\\s*:\\s*\\\"(?<id>\\{[^\\\"]+\\})\\\"(?:(?!\\\"device_id\\\").)*?\\\"friendly_name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]*)\\\"",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match match in idFirst)
        {
            candidates.Add(new SunshineDisplayCandidate(
                match.Groups["id"].Value,
                match.Groups["name"].Value,
                match.Value));
        }
        var nameFirst = Regex.Matches(
            log,
            "\\\"friendly_name\\\"\\s*:\\s*\\\"(?<name>[^\\\"]*)\\\"(?:(?!\\\"friendly_name\\\").)*?\\\"device_id\\\"\\s*:\\s*\\\"(?<id>\\{[^\\\"]+\\})\\\"",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        foreach (Match match in nameFirst)
        {
            if (candidates.Any(candidate => candidate.Id.Equals(match.Groups["id"].Value, StringComparison.OrdinalIgnoreCase))) continue;
            candidates.Add(new SunshineDisplayCandidate(
                match.Groups["id"].Value,
                match.Groups["name"].Value,
                match.Value));
        }
        return candidates;
    }

    private static int FindJsonArrayEnd(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }
            if (character == '"') inString = true;
            else if (character == '[') depth++;
            else if (character == ']' && --depth == 0) return index;
        }
        return -1;
    }

    private static bool IsManagedDisplayIdentity(string identity) =>
        identity.Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("Virtual Display", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("IddSample", StringComparison.OrdinalIgnoreCase) ||
        (identity.Contains("MTT", StringComparison.OrdinalIgnoreCase) &&
         identity.Contains("1337", StringComparison.OrdinalIgnoreCase));

    private sealed record SunshineDisplayCandidate(string Id, string Name, string Raw);

    internal static bool IsNativeDisplayManagementReady(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "sunshine.conf");
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path);
            return HasConfigurationValue(lines, "dd_configuration_option", "ensure_only_display") &&
                   HasConfigurationValue(lines, "dd_resolution_option", "auto") &&
                   HasConfigurationValue(lines, "dd_refresh_rate_option", "manual") &&
                   HasConfigurationValue(lines, "dd_manual_refresh_rate", "60") &&
                   HasConfigurationValue(lines, "dd_mode_remapping", VitaDisplayModeRemapping) &&
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
