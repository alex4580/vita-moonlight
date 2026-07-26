using System.Diagnostics;
using System.Security.Cryptography;
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
        "{\"final_resolution\":\"960x544\"}],\"refresh_rate_only\":[]}";

    internal static SunshineConfigurationResult Configure(HostSettings settings, string companionPath)
    {
        var configDirectory =
            InstallationTrust.RequireTrustedConfigurationDirectory(
                ResolveConfigurationDirectory(
                    settings.SunshineConfigDirectory,
                    settings.HostMode),
                "Streaming-host setup");
        Directory.CreateDirectory(configDirectory);
        var appsPath = Path.Combine(configDirectory, "apps.json");
        var sunshineConfigPath = Path.Combine(configDirectory, "sunshine.conf");
        var ownershipState = SunshineOwnershipJournal.Load();
        var ownership = SunshineOwnershipJournal.GetOrAddLocation(
            ownershipState,
            configDirectory);
        var backup = BackupOnce(appsPath);
        if (backup.Created)
        {
            ownership.BackupOwned = true;
            ownership.BackupSha256 = backup.Sha256;
        }
        var backupPath = backup.Path;

        var root = File.Exists(appsPath)
            ? JsonNode.Parse(File.ReadAllText(appsPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        var apps = root["apps"] as JsonArray ?? new JsonArray();
        root["apps"] = apps;

        foreach (var app in apps.OfType<JsonObject>())
        {
            RemoveOwnedHooks(app, ownership.Hooks);
            var applicationName =
                app["name"]?.GetValue<string>() ?? string.Empty;
            var legacyGenerated =
                string.Equals(
                    applicationName,
                    settings.SunshineApplicationName,
                    StringComparison.OrdinalIgnoreCase) &&
                HasLegacyGeneratedHook(app);
            var removedLegacyHooks =
                RemoveLegacyGeneratedHooks(app);
            if (legacyGenerated &&
                removedLegacyHooks > 0 &&
                IsUnchangedGeneratedManagedApplication(app) &&
                !ownership.CreatedApplications.Contains(
                    applicationName,
                    StringComparer.OrdinalIgnoreCase))
            {
                ownership.CreatedApplications.Add(applicationName);
            }
        }
        ownership.Hooks.Clear();

        var useNativeDisplayManagement = settings.HostMode == "sunshine" && settings.IntegrateAllSunshineApps;
        var managedApp = apps.OfType<JsonObject>().FirstOrDefault(candidate =>
            string.Equals(candidate["name"]?.GetValue<string>(), settings.SunshineApplicationName, StringComparison.OrdinalIgnoreCase));
        if (!useNativeDisplayManagement && managedApp is null)
        {
            managedApp = new JsonObject
            {
                ["name"] = settings.SunshineApplicationName,
                ["cmd"] = string.Empty,
                ["output"] = string.Empty,
                ["exclude-global-prep-cmd"] = false,
            };
            apps.Add(managedApp);
            if (!ownership.CreatedApplications.Contains(
                    settings.SunshineApplicationName,
                    StringComparer.OrdinalIgnoreCase))
            {
                ownership.CreatedApplications.Add(settings.SunshineApplicationName);
            }
        }

        var applicationObjects = apps.OfType<JsonObject>().ToArray();
        var targets = useNativeDisplayManagement ? Array.Empty<JsonObject>() : new[] { managedApp };
        foreach (var app in targets)
        {
            var hook = AddManagedHook(app!, companionPath);
            ownership.Hooks.Add(hook);
        }

        var configurationLines = File.Exists(sunshineConfigPath)
            ? File.ReadAllLines(sunshineConfigPath).ToList()
            : new List<string>();
        UpdateSunshineConfiguration(configurationLines, ownership);
        if (useNativeDisplayManagement)
        {
            var displayDeviceId = WaitForManagedDisplayDeviceId(
                Path.Combine(configDirectory, "sunshine.log"),
                settings.DisplayMatch,
                timeoutMilliseconds: 30000,
                pollMilliseconds: 250);
            if (displayDeviceId is null)
            {
                throw new InvalidOperationException(
                    "Sunshine did not enumerate the Vita virtual display within 30 seconds. " +
                    "Restart Windows if the display driver was just installed, then open Get started and choose Set up or repair this PC.");
            }
            ConfigureNativeDisplayManagement(
                configurationLines,
                displayDeviceId,
                settings.ForceSdr,
                ownership);
        }
        else if (settings.HostMode == "sunshine")
        {
            SetOwnedConfigurationValue(
                configurationLines,
                ownership,
                "dd_configuration_option",
                "disabled");
            SetOwnedConfigurationValue(
                configurationLines,
                ownership,
                "dd_config_revert_on_disconnect",
                "disabled");
        }
        SunshineOwnershipJournal.Save(ownershipState);
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

    internal static SunshineIntegrationCleanupResult RemoveManagedIntegration()
    {
        var ownershipState = SunshineOwnershipJournal.Load();
        var removedHooks = 0;
        var removedGeneratedApplication = false;
        var removedNativeDisplaySettings = false;
        var cleanedDirectories = new List<string>();
        foreach (var ownership in ownershipState.Locations)
        {
            var locationRemovedHooks = 0;
            var locationRemovedApplication = false;
            var configDirectory =
                SunshineOwnershipJournal.ValidateConfigurationDirectory(
                    InstallationTrust
                        .RequireTrustedConfigurationDirectory(
                            ownership.ConfigurationDirectory,
                            "Streaming-host integration cleanup"));
            cleanedDirectories.Add(configDirectory);
            var appsPath = Path.Combine(configDirectory, "apps.json");
            var sunshineConfigPath = Path.Combine(configDirectory, "sunshine.conf");
            var backupPath = appsPath + ".vita-moonlight.backup";

            if (File.Exists(appsPath))
            {
                SunshineOwnershipJournal.ValidateOwnedFile(appsPath);
                var root = JsonNode.Parse(File.ReadAllText(appsPath))?.AsObject()
                    ?? throw new InvalidDataException("Sunshine apps.json has no root object.");
                if (root["apps"] is JsonArray apps)
                {
                    foreach (var app in apps.OfType<JsonObject>())
                    {
                        locationRemovedHooks += RemoveOwnedHooks(
                            app,
                            ownership.Hooks);
                    }

                    foreach (var applicationName in ownership.CreatedApplications)
                    {
                        var managedApp = apps.OfType<JsonObject>().FirstOrDefault(candidate =>
                            string.Equals(
                                candidate["name"]?.GetValue<string>(),
                                applicationName,
                                StringComparison.OrdinalIgnoreCase));
                        if (managedApp is not null &&
                            IsUnchangedGeneratedManagedApplication(managedApp))
                        {
                            apps.Remove(managedApp);
                            locationRemovedApplication = true;
                        }
                    }
                }

                if (locationRemovedHooks > 0 || locationRemovedApplication)
                {
                    DisplayTopologyService.AtomicWrite(
                        appsPath,
                        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            removedHooks += locationRemovedHooks;
            removedGeneratedApplication |= locationRemovedApplication;

            if (File.Exists(sunshineConfigPath))
            {
                SunshineOwnershipJournal.ValidateOwnedFile(sunshineConfigPath);
                var lines = File.ReadAllLines(sunshineConfigPath).ToList();
                if (RestoreOwnedConfiguration(lines, ownership))
                {
                    removedNativeDisplaySettings = true;
                    DisplayTopologyService.AtomicWrite(
                        sunshineConfigPath,
                        lines.Count == 0
                            ? string.Empty
                            : string.Join(Environment.NewLine, lines) + Environment.NewLine);
                }
            }

            if (ownership.BackupOwned &&
                !string.IsNullOrWhiteSpace(ownership.BackupSha256) &&
                File.Exists(backupPath))
            {
                SunshineOwnershipJournal.ValidateOwnedFile(backupPath);
                var currentHash = ComputeFileSha256(backupPath);
                if (FixedTimeHexEquals(
                        currentHash,
                        ownership.BackupSha256))
                {
                    File.Delete(backupPath);
                }
            }
        }
        SunshineOwnershipJournal.Delete();

        return new SunshineIntegrationCleanupResult(
            string.Join("; ", cleanedDirectories),
            removedHooks,
            removedGeneratedApplication,
            removedNativeDisplaySettings);
    }

    private static int RemoveOwnedHooks(
        JsonObject app,
        IReadOnlyCollection<SunshineOwnedHook> ownedHooks)
    {
        if (app["prep-cmd"] is not JsonArray prepCommands) return 0;
        var applicationName = app["name"]?.GetValue<string>() ?? string.Empty;
        var removed = 0;
        for (var index = prepCommands.Count - 1; index >= 0; index--)
        {
            if (prepCommands[index] is not JsonObject existing) continue;
            var doCommand = existing["do"]?.GetValue<string>() ?? string.Empty;
            var undoCommand = existing["undo"]?.GetValue<string>() ?? string.Empty;
            var elevated = existing["elevated"]?.GetValue<bool>() ?? false;
            if (ownedHooks.Any(hook =>
                    string.Equals(
                        hook.ApplicationName,
                        applicationName,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(hook.Do, doCommand, StringComparison.Ordinal) &&
                    string.Equals(hook.Undo, undoCommand, StringComparison.Ordinal) &&
                    hook.Elevated == elevated))
            {
                prepCommands.RemoveAt(index);
                removed++;
            }
        }
        if (removed > 0 && prepCommands.Count == 0)
        {
            app.Remove("prep-cmd");
        }
        return removed;
    }

    private static int RemoveLegacyGeneratedHooks(JsonObject app)
    {
        if (app["prep-cmd"] is not JsonArray prepCommands) return 0;
        var removed = 0;
        for (var index = prepCommands.Count - 1; index >= 0; index--)
        {
            if (prepCommands[index] is not JsonObject existing) continue;
            var doCommand = existing["do"]?.GetValue<string>() ?? string.Empty;
            var undoCommand = existing["undo"]?.GetValue<string>() ?? string.Empty;
            var elevated = existing["elevated"]?.GetValue<bool>() ?? false;
            if (elevated &&
                IsGeneratedStartCommand(doCommand) &&
                IsGeneratedStopCommand(undoCommand))
            {
                prepCommands.RemoveAt(index);
                removed++;
            }
        }
        if (removed > 0 && prepCommands.Count == 0)
        {
            app.Remove("prep-cmd");
        }
        return removed;
    }

    private static bool HasLegacyGeneratedHook(JsonObject app)
    {
        if (app["prep-cmd"] is not JsonArray prepCommands)
        {
            return false;
        }
        return prepCommands.OfType<JsonObject>().Any(existing =>
            (existing["elevated"]?.GetValue<bool>() ?? false) &&
            IsGeneratedStartCommand(
                existing["do"]?.GetValue<string>() ?? string.Empty) &&
            IsGeneratedStopCommand(
                existing["undo"]?.GetValue<string>() ?? string.Empty));
    }

    private static bool IsGeneratedStartCommand(string command) =>
        command.StartsWith(
            "cmd.exe /D /S /C \"\"",
            StringComparison.OrdinalIgnoreCase) &&
        command.Contains(
            $"{HookMarker}.exe\" session start --width %SUNSHINE_CLIENT_WIDTH% " +
            "--height %SUNSHINE_CLIENT_HEIGHT% --fps %SUNSHINE_CLIENT_FPS%",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsGeneratedStopCommand(string command) =>
        command.StartsWith('"') &&
        command.EndsWith(
            $"{HookMarker}.exe\" session stop",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsUnchangedGeneratedManagedApplication(JsonObject app)
    {
        var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "name",
            "cmd",
            "output",
            "exclude-global-prep-cmd",
            "prep-cmd",
        };
        return app.All(property => knownKeys.Contains(property.Key)) &&
               string.IsNullOrEmpty(app["cmd"]?.GetValue<string>()) &&
               string.IsNullOrEmpty(app["output"]?.GetValue<string>()) &&
               app["exclude-global-prep-cmd"]?.GetValue<bool>() == false &&
               (app["prep-cmd"] is null ||
                app["prep-cmd"] is JsonArray { Count: 0 });
    }

    internal static bool RestoreOwnedConfiguration(
        List<string> lines,
        SunshineOwnedLocation ownership)
    {
        var changed = false;
        foreach (var owned in ownership.Values)
        {
            if (!TryGetConfigurationValue(lines, owned.Key, out var currentValue) ||
                !string.Equals(
                    currentValue,
                    owned.Value.AppliedValue,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (owned.Value.OriginalPresent)
            {
                SetConfigurationValue(
                    lines,
                    owned.Key,
                    owned.Value.OriginalValue ?? string.Empty);
            }
            else
            {
                RemoveConfigurationValue(lines, owned.Key);
            }
            changed = true;
        }
        return changed;
    }

    private static SunshineOwnedHook AddManagedHook(
        JsonObject app,
        string companionPath)
    {
        var prepCommands = app["prep-cmd"] as JsonArray ?? new JsonArray();
        app["prep-cmd"] = prepCommands;
        var doCommand = BuildStartCommand(companionPath);
        var undoCommand = BuildStopCommand(companionPath);
        prepCommands.Add(new JsonObject
        {
            ["do"] = doCommand,
            ["undo"] = undoCommand,
            ["elevated"] = true,
        });
        return new SunshineOwnedHook(
            app["name"]?.GetValue<string>() ?? string.Empty,
            doCommand,
            undoCommand,
            true);
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

    internal static void UpdateSunshineConfiguration(List<string> lines) =>
        UpdateSunshineConfiguration(lines, null);

    private static void UpdateSunshineConfiguration(
        List<string> lines,
        SunshineOwnedLocation? ownership)
    {
        SetConfigurationValue(lines, ownership, "controller", "enabled");
        SetConfigurationValue(lines, ownership, "gamepad", "auto");
        SetConfigurationValue(lines, ownership, "motion_as_ds4", "enabled");
        SetConfigurationValue(lines, ownership, "touchpad_as_ds4", "enabled");
        SetConfigurationValue(lines, ownership, "keyboard", "enabled");
        SetConfigurationValue(lines, ownership, "mouse", "enabled");
        SetConfigurationValue(lines, ownership, "native_pen_touch", "enabled");
    }

    internal static void ConfigureNativeDisplayManagement(
        List<string> lines,
        string displayDeviceId,
        bool forceSdr) =>
        ConfigureNativeDisplayManagement(lines, displayDeviceId, forceSdr, null);

    private static void ConfigureNativeDisplayManagement(
        List<string> lines,
        string displayDeviceId,
        bool forceSdr,
        SunshineOwnedLocation? ownership)
    {
        SetConfigurationValue(lines, ownership, "output_name", displayDeviceId);
        SetConfigurationValue(lines, ownership, "dd_configuration_option", "ensure_only_display");
        SetConfigurationValue(lines, ownership, "dd_resolution_option", "auto");
        // Keep the Windows desktop at a driver-safe 60 Hz. The client encoder
        // can still stream at 24/30/40/50/60 FPS independently.
        SetConfigurationValue(lines, ownership, "dd_refresh_rate_option", "manual");
        SetConfigurationValue(lines, ownership, "dd_manual_refresh_rate", "60");
        SetConfigurationValue(lines, ownership, "dd_mode_remapping", VitaDisplayModeRemapping);
        SetConfigurationValue(lines, ownership, "dd_hdr_option", forceSdr ? "auto" : "disabled");
        SetConfigurationValue(lines, ownership, "dd_config_revert_delay", "500");
        SetConfigurationValue(lines, ownership, "dd_config_revert_on_disconnect", "enabled");
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

    internal static string? WaitForManagedDisplayDeviceId(
        string logPath,
        string? displayMatch,
        int timeoutMilliseconds,
        int pollMilliseconds)
    {
        if (timeoutMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
        }
        if (pollMilliseconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pollMilliseconds));
        }

        var timer = Stopwatch.StartNew();
        while (true)
        {
            var displayDeviceId = FindManagedDisplayDeviceId(logPath, displayMatch);
            if (displayDeviceId is not null) return displayDeviceId;
            if (timer.ElapsedMilliseconds >= timeoutMilliseconds) return null;
            var remaining = timeoutMilliseconds - timer.ElapsedMilliseconds;
            Thread.Sleep((int)Math.Min(pollMilliseconds, Math.Max(1, remaining)));
        }
    }

    private static IReadOnlyList<SunshineDisplayCandidate> ParseDisplayCandidates(string log)
    {
        var candidates = new List<SunshineDisplayCandidate>();
        const string marker = "Currently available display devices:";
        var markerIndex = log.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return candidates;
        }

        // Never fall back to an older inventory. While Sunshine is writing a
        // new block, returning an earlier device ID can bind output_name to a
        // display that no longer exists. Polling will retry once this newest
        // inventory is complete.
        var arrayStart = log.IndexOf('[', markerIndex + marker.Length);
        if (arrayStart < 0)
        {
            return candidates;
        }
        var arrayEnd = FindJsonArrayEnd(log, arrayStart);
        if (arrayEnd < 0)
        {
            return candidates;
        }

        var inventory = log[arrayStart..(arrayEnd + 1)];
        try
        {
            using var document = JsonDocument.Parse(inventory);
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
            // Older or development Sunshine builds may emit non-strict JSON;
            // the regex fallback below remains restricted to the newest block.
        }
        if (candidates.Count > 0) return candidates;

        var idFirst = Regex.Matches(
            inventory,
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
            inventory,
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
        identity.Contains("MttVDD", StringComparison.OrdinalIgnoreCase) ||
        identity.Contains("MTT1337", StringComparison.OrdinalIgnoreCase) ||
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

    private static void SetConfigurationValue(
        List<string> lines,
        SunshineOwnedLocation? ownership,
        string key,
        string value)
    {
        if (ownership is not null)
        {
            if (ownership.Values.TryGetValue(key, out var existing))
            {
                ownership.Values[key] = existing with { AppliedValue = value };
            }
            else
            {
                var originalPresent = TryGetConfigurationValue(
                    lines,
                    key,
                    out var originalValue);
                ownership.Values[key] = new SunshineOwnedValue(
                    originalPresent,
                    originalPresent ? originalValue : null,
                    value);
            }
        }
        SetConfigurationValue(lines, key, value);
    }

    private static void SetOwnedConfigurationValue(
        List<string> lines,
        SunshineOwnedLocation ownership,
        string key,
        string value) =>
        SetConfigurationValue(lines, ownership, key, value);

    private static void SetConfigurationValue(List<string> lines, string key, string value)
    {
        var index = FindConfigurationValueIndex(lines, key);
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

    private static bool TryGetConfigurationValue(
        IReadOnlyList<string> lines,
        string key,
        out string value)
    {
        var index = FindConfigurationValueIndex(lines, key);
        if (index < 0)
        {
            value = string.Empty;
            return false;
        }
        var equals = lines[index].IndexOf('=');
        value = equals < 0 ? string.Empty : lines[index][(equals + 1)..].Trim();
        return true;
    }

    private static int FindConfigurationValueIndex(
        IReadOnlyList<string> lines,
        string key)
    {
        for (var index = 0; index < lines.Count; index++)
        {
            var equals = lines[index].IndexOf('=');
            if (equals < 0) continue;
            if (string.Equals(
                    lines[index][..equals].Trim(),
                    key,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }

    private static void RemoveConfigurationValue(List<string> lines, string key)
    {
        var index = FindConfigurationValueIndex(lines, key);
        if (index >= 0)
        {
            lines.RemoveAt(index);
        }
    }

    private static BackupResult BackupOnce(string path)
    {
        var backupPath = path + ".vita-moonlight.backup";
        var created = false;
        if (File.Exists(path) && !File.Exists(backupPath))
        {
            File.Copy(path, backupPath);
            created = true;
        }
        return new BackupResult(
            backupPath,
            created,
            created ? ComputeFileSha256(backupPath) : null);
    }

    private static string ComputeFileSha256(string path) =>
        Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path)));

    private static bool FixedTimeHexEquals(
        string first,
        string second)
    {
        try
        {
            var firstBytes = Convert.FromHexString(first);
            var secondBytes = Convert.FromHexString(second);
            return firstBytes.Length == secondBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(
                       firstBytes,
                       secondBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record BackupResult(
        string Path,
        bool Created,
        string? Sha256);

    private static void ValidateExecutablePath(string companionPath)
    {
        if (string.IsNullOrWhiteSpace(companionPath) || companionPath.Contains('"'))
        {
            throw new ArgumentException("The companion executable path is invalid.", nameof(companionPath));
        }
    }
}
