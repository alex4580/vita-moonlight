using System.Diagnostics;
using System.Globalization;
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
        "{\"requested_resolution\":\"1280x720\",\"final_resolution\":\"1280x720\"}]," +
        "\"refresh_rate_only\":[]}";

    internal static SunshineConfigurationResult Configure(
        HostSettings settings,
        string companionPath,
        DateTimeOffset? inventoryNotBeforeUtc = null)
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
        var backupPath = appsPath + ".vita-moonlight.backup";
        SunshineOwnershipJournal.ValidateOwnedFile(appsPath);
        SunshineOwnershipJournal.ValidateOwnedFile(sunshineConfigPath);
        SunshineOwnershipJournal.ValidateOwnedFile(backupPath);
        var originalApps = CaptureFile(appsPath);
        var originalConfiguration = CaptureFile(sunshineConfigPath);
        var ownershipState = SunshineOwnershipJournal.Load();
        var originalOwnershipState =
            SunshineOwnershipJournal.Clone(ownershipState);
        var ownership = SunshineOwnershipJournal.GetOrAddLocation(
            ownershipState,
            configDirectory,
            settings.HostMode);

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
        if (useNativeDisplayManagement &&
            managedApp is not null &&
            ownership.CreatedApplications.Contains(
                settings.SunshineApplicationName,
                StringComparer.OrdinalIgnoreCase) &&
            IsUnchangedGeneratedManagedApplication(managedApp))
        {
            // Older/manual configurations created a no-op launcher solely to
            // carry the display hook. Native all-app lifecycle management no
            // longer needs it. Remove only the exact journal-owned, unchanged
            // generated app; a user-customized launcher is preserved.
            apps.Remove(managedApp);
            ownership.CreatedApplications.RemoveAll(applicationName =>
                string.Equals(
                    applicationName,
                    settings.SunshineApplicationName,
                    StringComparison.OrdinalIgnoreCase));
            managedApp = null;
        }
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
                pollMilliseconds: 250,
                inventoryNotBeforeUtc: inventoryNotBeforeUtc);
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
        var appsContent = root.ToJsonString(
            new JsonSerializerOptions { WriteIndented = true });
        var configurationContent =
            string.Join(Environment.NewLine, configurationLines) +
            Environment.NewLine;
        var createBackup = originalApps.Content is not null &&
            !File.Exists(backupPath);
        var newBackupSha256 = createBackup
            ? ComputeSha256(originalApps.Content!)
            : null;
        if (createBackup)
        {
            // Publish ownership before creating the backup as well as before
            // replacing either host file. A killed setup can therefore never
            // leave a newly-created, permanently unowned backup behind.
            ownership.BackupOwned = true;
            ownership.BackupSha256 = newBackupSha256;
        }
        var backupCreatedDuringAttempt = false;
        try
        {
            // Publish ownership before either host file. If the process is
            // interrupted between atomic file replacements, every completed
            // mutation is still attributable and a retry/uninstall can remove
            // only the exact values and hooks this product applied.
            SunshineOwnershipJournal.Save(ownershipState);
            if (createBackup)
            {
                AtomicWriteBytes(backupPath, originalApps.Content!);
                backupCreatedDuringAttempt = true;
                if (!FixedTimeHexEquals(
                        ComputeFileSha256(backupPath),
                        newBackupSha256!))
                {
                    throw new IOException(
                        "Windows did not verify the complete original apps.json backup.");
                }
            }
            DisplayTopologyService.AtomicWrite(appsPath, appsContent);
            DisplayTopologyService.AtomicWrite(
                sunshineConfigPath,
                configurationContent);
            VerifyWrittenText(appsPath, appsContent);
            VerifyWrittenText(sunshineConfigPath, configurationContent);
        }
        catch (Exception updateError)
        {
            var rollbackErrors = new List<Exception>();
            TryRollbackFile(appsPath, originalApps, rollbackErrors);
            TryRollbackFile(
                sunshineConfigPath,
                originalConfiguration,
                rollbackErrors);
            if (backupCreatedDuringAttempt)
            {
                try
                {
                    if (File.Exists(backupPath) &&
                        FixedTimeHexEquals(
                            ComputeFileSha256(backupPath),
                            newBackupSha256!))
                    {
                        File.Delete(backupPath);
                    }
                    else if (File.Exists(backupPath))
                    {
                        throw new InvalidDataException(
                            "The newly-created apps backup changed before configuration rollback.");
                    }
                }
                catch (Exception backupError)
                {
                    rollbackErrors.Add(backupError);
                }
            }
            if (rollbackErrors.Count == 0)
            {
                try
                {
                    SunshineOwnershipJournal.Replace(
                        originalOwnershipState);
                }
                catch (Exception ownershipRollbackError)
                {
                    rollbackErrors.Add(ownershipRollbackError);
                }
            }

            if (rollbackErrors.Count == 0)
            {
                throw new InvalidOperationException(
                    "Streaming-host configuration failed; apps.json, sunshine.conf, and the ownership journal were restored to their original state.",
                    updateError);
            }
            throw new AggregateException(
                "Streaming-host configuration failed and could not be rolled back completely. " +
                "The ownership journal was retained so repair or uninstall can retry exact cleanup.",
                new[] { updateError }.Concat(rollbackErrors));
        }
        return new SunshineConfigurationResult(
            configDirectory,
            settings.SunshineApplicationName,
            useNativeDisplayManagement ? applicationObjects.Length : targets.Length,
            useNativeDisplayManagement,
            backupPath);
    }

    private sealed record ConfigurationFileSnapshot(byte[]? Content);

    private static ConfigurationFileSnapshot CaptureFile(string path) =>
        new(File.Exists(path) ? File.ReadAllBytes(path) : null);

    private static void TryRollbackFile(
        string path,
        ConfigurationFileSnapshot snapshot,
        ICollection<Exception> errors)
    {
        try
        {
            if (snapshot.Content is null)
            {
                if (File.Exists(path)) File.Delete(path);
            }
            else
            {
                AtomicWriteBytes(path, snapshot.Content);
            }
        }
        catch (Exception error)
        {
            errors.Add(new IOException(
                $"Could not restore {path} during configuration rollback.",
                error));
        }
    }

    private static void AtomicWriteBytes(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path) ??
            throw new InvalidOperationException(
                "The configuration destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void VerifyWrittenText(string path, string expected)
    {
        if (!File.Exists(path) ||
            !string.Equals(
                File.ReadAllText(path),
                expected,
                StringComparison.Ordinal))
        {
            throw new IOException(
                $"Windows did not verify the complete configuration write to {path}.");
        }
    }

    internal static SunshineIntegrationCleanupResult RemoveManagedIntegration() =>
        RemoveManagedIntegration(hostMode: null);

    internal static void RequireManagedIntegrationCleanupReady()
    {
        var ownershipState = SunshineOwnershipJournal.Load();
        var settings = HostSettings.Load();
        SunshineOwnershipJournal.TagExistingLocation(
            ownershipState,
            ResolveConfigurationDirectory(
                settings.SunshineConfigDirectory,
                settings.HostMode),
            settings.HostMode);
        if (ownershipState.Locations.Any(location =>
                SunshineOwnershipJournal.LocationMatchesHost(
                    location,
                    "apollo")) &&
            StreamingHostLocator.IsApolloRunning())
        {
            throw new InvalidOperationException(
                "Apollo is still running. Exit Apollo from its tray icon before uninstall so exact Vita-owned hooks can be removed before any shared dependency is changed.");
        }

        foreach (var ownership in ownershipState.Locations)
        {
            var configDirectory =
                SunshineOwnershipJournal.ValidateConfigurationDirectory(
                    InstallationTrust.RequireTrustedConfigurationDirectory(
                        ownership.ConfigurationDirectory,
                        "Streaming-host integration cleanup preflight"));
            var appsPath = Path.Combine(configDirectory, "apps.json");
            var sunshineConfigPath = Path.Combine(
                configDirectory,
                "sunshine.conf");
            var backupPath = appsPath + ".vita-moonlight.backup";
            SunshineOwnershipJournal.ValidateOwnedFile(appsPath);
            SunshineOwnershipJournal.ValidateOwnedFile(sunshineConfigPath);
            SunshineOwnershipJournal.ValidateOwnedFile(backupPath);
            if (File.Exists(appsPath))
            {
                _ = JsonNode.Parse(File.ReadAllText(appsPath))?.AsObject()
                    ?? throw new InvalidDataException(
                        "Streaming-host apps.json has no root object.");
            }
            if (File.Exists(sunshineConfigPath))
            {
                _ = File.ReadAllLines(sunshineConfigPath);
            }
        }
    }

    internal static SunshineIntegrationCleanupResult
        RemoveManagedIntegrationForHost(string hostMode) =>
        RemoveManagedIntegration(hostMode);

    private static SunshineIntegrationCleanupResult RemoveManagedIntegration(
        string? hostMode)
    {
        var ownershipState = SunshineOwnershipJournal.Load();
        if (hostMode is null)
        {
            // Journals written before host-mode ownership was recorded are
            // tagged from the still-present protected settings before an
            // uninstall decides whether Apollo must be closed.
            var settings = HostSettings.Load();
            SunshineOwnershipJournal.TagExistingLocation(
                ownershipState,
                ResolveConfigurationDirectory(
                    settings.SunshineConfigDirectory,
                    settings.HostMode),
                settings.HostMode);
        }
        var selectedLocations = ownershipState.Locations
            .Where(location =>
                hostMode is null ||
                SunshineOwnershipJournal.LocationMatchesHost(
                    location,
                    hostMode))
            .ToArray();
        if (selectedLocations.Any(location =>
                SunshineOwnershipJournal.LocationMatchesHost(
                    location,
                    "apollo")) &&
            StreamingHostLocator.IsApolloRunning())
        {
            throw new InvalidOperationException(
                "Apollo is still running. Exit Apollo from its tray icon, then retry so Vita-owned hooks can be removed without racing Apollo's configuration writer.");
        }
        var removedHooks = 0;
        var removedGeneratedApplication = false;
        var removedNativeDisplaySettings = false;
        var cleanedDirectories = new List<string>();
        foreach (var ownership in selectedLocations)
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

            ownershipState.Locations.Remove(ownership);
        }
        if (ownershipState.Locations.Count == 0)
        {
            SunshineOwnershipJournal.Delete();
        }
        else
        {
            SunshineOwnershipJournal.Save(ownershipState);
        }

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

    private static bool IsGeneratedStartCommand(string command)
    {
        if (!command.StartsWith(
                "cmd.exe /D /S /C \"\"",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var arguments =
            "--width %SUNSHINE_CLIENT_WIDTH% " +
            "--height %SUNSHINE_CLIENT_HEIGHT% --fps %SUNSHINE_CLIENT_FPS%";
        return command.Contains(
                   $"{HookMarker}.exe\" session hook-start {arguments}",
                   StringComparison.OrdinalIgnoreCase) ||
               // Recognize hooks from earlier releases so an upgrade removes
               // the strict boundary before installing the tolerant one.
               command.Contains(
                   $"{HookMarker}.exe\" session start {arguments}",
                   StringComparison.OrdinalIgnoreCase);
    }

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
        return $"cmd.exe /D /S /C \"\"{companionPath}\" session hook-start --width %SUNSHINE_CLIENT_WIDTH% --height %SUNSHINE_CLIENT_HEIGHT% --fps %SUNSHINE_CLIENT_FPS%\"";
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

    internal static string? FindManagedDisplayDeviceId(
        string logPath,
        string? displayMatch,
        DateTimeOffset? inventoryNotBeforeUtc = null)
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

        var inventory = ParseLatestDisplayInventory(log);
        if (inventoryNotBeforeUtc is { } minimum &&
            (inventory.ObservedAt is null ||
             inventory.ObservedAt.Value.ToUniversalTime()
                 .AddMilliseconds(2) <
             minimum.ToUniversalTime()))
        {
            return null;
        }
        var candidates = inventory.Candidates;
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
        int pollMilliseconds,
        DateTimeOffset? inventoryNotBeforeUtc = null)
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
            var displayDeviceId = FindManagedDisplayDeviceId(
                logPath,
                displayMatch,
                inventoryNotBeforeUtc);
            if (displayDeviceId is not null) return displayDeviceId;
            if (timer.ElapsedMilliseconds >= timeoutMilliseconds) return null;
            var remaining = timeoutMilliseconds - timer.ElapsedMilliseconds;
            Thread.Sleep((int)Math.Min(pollMilliseconds, Math.Max(1, remaining)));
        }
    }

    private static SunshineDisplayInventory ParseLatestDisplayInventory(
        string log)
    {
        var candidates = new List<SunshineDisplayCandidate>();
        const string marker = "Currently available display devices:";
        var markerIndex = log.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return new SunshineDisplayInventory(null, candidates);
        }
        var observedAt = ParseInventoryTimestamp(log, markerIndex);

        // Never fall back to an older inventory. While Sunshine is writing a
        // new block, returning an earlier device ID can bind output_name to a
        // display that no longer exists. Polling will retry once this newest
        // inventory is complete.
        var arrayStart = log.IndexOf('[', markerIndex + marker.Length);
        if (arrayStart < 0)
        {
            return new SunshineDisplayInventory(observedAt, candidates);
        }
        var arrayEnd = FindJsonArrayEnd(log, arrayStart);
        if (arrayEnd < 0)
        {
            return new SunshineDisplayInventory(observedAt, candidates);
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
        if (candidates.Count > 0)
        {
            return new SunshineDisplayInventory(observedAt, candidates);
        }

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
        return new SunshineDisplayInventory(observedAt, candidates);
    }

    private static DateTimeOffset? ParseInventoryTimestamp(
        string log,
        int markerIndex)
    {
        var lineStart = markerIndex > 0
            ? log.LastIndexOf('\n', markerIndex - 1)
            : -1;
        var header = log[(lineStart + 1)..markerIndex];
        var match = Regex.Match(
            header,
            @"\[(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\]",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return null;
        }
        return DateTimeOffset.TryParseExact(
            match.Groups["timestamp"].Value,
            "yyyy-MM-dd HH:mm:ss.fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var timestamp)
            ? timestamp
            : null;
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

    private sealed record SunshineDisplayInventory(
        DateTimeOffset? ObservedAt,
        IReadOnlyList<SunshineDisplayCandidate> Candidates);

    internal static bool IsNativeDisplayManagementReady(
        string configDirectory,
        bool forceSdr,
        string? displayMatch = null,
        DateTimeOffset? inventoryNotBeforeUtc = null)
    {
        var path = Path.Combine(configDirectory, "sunshine.conf");
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path);
            if (!TryGetConfigurationValue(
                    lines,
                    "output_name",
                    out var configuredDisplayId) ||
                string.IsNullOrWhiteSpace(configuredDisplayId))
            {
                return false;
            }
            var enumeratedDisplayId = FindManagedDisplayDeviceId(
                Path.Combine(configDirectory, "sunshine.log"),
                displayMatch,
                inventoryNotBeforeUtc);
            return HasConfigurationValue(lines, "dd_configuration_option", "ensure_only_display") &&
                   HasConfigurationValue(lines, "dd_resolution_option", "auto") &&
                   HasConfigurationValue(lines, "dd_refresh_rate_option", "manual") &&
                   HasConfigurationValue(lines, "dd_manual_refresh_rate", "60") &&
                   HasConfigurationValue(lines, "dd_mode_remapping", VitaDisplayModeRemapping) &&
                   HasConfigurationValue(lines, "dd_hdr_option", forceSdr ? "auto" : "disabled") &&
                   HasConfigurationValue(lines, "dd_config_revert_delay", "500") &&
                   HasConfigurationValue(lines, "dd_config_revert_on_disconnect", "enabled") &&
                   HasConfigurationValue(lines, "controller", "enabled") &&
                   HasConfigurationValue(lines, "gamepad", "auto") &&
                   HasConfigurationValue(lines, "motion_as_ds4", "enabled") &&
                   HasConfigurationValue(lines, "touchpad_as_ds4", "enabled") &&
                   HasConfigurationValue(lines, "keyboard", "enabled") &&
                   HasConfigurationValue(lines, "mouse", "enabled") &&
                   HasConfigurationValue(lines, "native_pen_touch", "enabled") &&
                   string.Equals(
                       configuredDisplayId,
                       enumeratedDisplayId,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsManagedHookReady(
        string configDirectory,
        string applicationName,
        string companionPath)
    {
        var appsPath = Path.Combine(configDirectory, "apps.json");
        if (!File.Exists(appsPath)) return false;
        try
        {
            var expected = new SunshineOwnedHook(
                applicationName,
                BuildStartCommand(companionPath),
                BuildStopCommand(companionPath),
                true);
            var normalizedDirectory = Path.GetFullPath(configDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var ownership = SunshineOwnershipJournal.Load().Locations
                .FirstOrDefault(location => string.Equals(
                    Path.GetFullPath(location.ConfigurationDirectory)
                        .TrimEnd(
                            Path.DirectorySeparatorChar,
                            Path.AltDirectorySeparatorChar),
                    normalizedDirectory,
                    StringComparison.OrdinalIgnoreCase));
            if (ownership is null ||
                !ownership.Hooks.Any(hook => hook == expected))
            {
                return false;
            }

            var root = JsonNode.Parse(File.ReadAllText(appsPath))?.AsObject();
            if (root?["apps"] is not JsonArray apps) return false;
            return apps.OfType<JsonObject>().Any(app =>
                string.Equals(
                    app["name"]?.GetValue<string>(),
                    applicationName,
                    StringComparison.OrdinalIgnoreCase) &&
                app["prep-cmd"] is JsonArray prepCommands &&
                prepCommands.OfType<JsonObject>().Any(command =>
                    string.Equals(
                        command["do"]?.GetValue<string>(),
                        expected.Do,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        command["undo"]?.GetValue<string>(),
                        expected.Undo,
                        StringComparison.Ordinal) &&
                    (command["elevated"]?.GetValue<bool>() ?? false) ==
                    expected.Elevated));
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                JsonException or
                InvalidDataException or
                InvalidOperationException or
                ArgumentException or
                System.Security.SecurityException)
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

    private static string ComputeFileSha256(string path) =>
        Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(path)));

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content));

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

    private static void ValidateExecutablePath(string companionPath)
    {
        if (string.IsNullOrWhiteSpace(companionPath) || companionPath.Contains('"'))
        {
            throw new ArgumentException("The companion executable path is invalid.", nameof(companionPath));
        }
    }
}
