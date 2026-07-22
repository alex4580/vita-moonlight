using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitInvalidArguments = 2;
    private const int ExitMissingRequiredComponent = 3;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "gui";
            var remaining = args.Skip(1).ToArray();
            return command switch
            {
                "gui" => RunControlPanel(),
                "doctor" => RunDoctor(HasFlag(remaining, "--json")),
                "profile" => PrintProfile(HasFlag(remaining, "--json")),
                "configure" => Configure(remaining),
                "host" => HostCommand(remaining),
                "driver" => DriverCommand(remaining),
                "display" => DisplayCommand(remaining),
                "session" => SessionCommand(remaining),
                "recovery" => RecoveryCommand(remaining),
                "agent" => AgentCommand(remaining),
                "emergency" => EmergencyCommand(remaining),
                "self-test" => RunSelfTest(),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => InvalidCommand(command),
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Error: {error.Message}");
            if (Environment.GetEnvironmentVariable("VITA_MOONLIGHT_DEBUG") == "1")
            {
                Console.Error.WriteLine(error);
            }
            return ExitFailure;
        }
    }

    private static int RunControlPanel()
    {
        EnsureWindows();
        HostControlPanel.Run();
        return ExitSuccess;
    }

    private static int Configure(string[] args)
    {
        EnsureWindows();
        var previous = HostSettings.Load();
        var hostMode = GetOption(args, "--host") ?? previous.HostMode;
        if (!hostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase) &&
            !hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--host must be `sunshine` or `apollo`.");
        }

        var settings = previous with
        {
            HostMode = hostMode.ToLowerInvariant(),
            SunshineConfigDirectory = GetOption(args, "--config-dir") ?? previous.SunshineConfigDirectory,
            SunshineApplicationName = GetOption(args, "--app") ?? previous.SunshineApplicationName,
            DisplayWizardPath = GetOption(args, "--driver-bundle") ?? GetOption(args, "--displaywizard") ?? previous.DisplayWizardPath,
            DisplayMatch = GetOption(args, "--display-match") ?? previous.DisplayMatch,
            IntegrateAllSunshineApps = GetOptionalBool(args, "--all-apps") ?? previous.IntegrateAllSunshineApps,
            ForceSdr = GetOptionalBool(args, "--force-sdr") ?? previous.ForceSdr,
        };

        if (settings.HostMode == "sunshine")
        {
            var wizard = DisplayWizardAdapter.Locate(settings.DisplayWizardPath);
            wizard.ValidateDriverBundle();
            settings = settings with { DisplayWizardPath = wizard.ExecutablePath };
        }

        var companionPath = GetOption(args, "--companion") ?? Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe");
        settings.Save();
        var result = SunshineConfigurator.Configure(settings, Path.GetFullPath(companionPath));
        Console.WriteLine($"Configured {settings.HostMode} in {result.ConfigurationDirectory}");
        Console.WriteLine($"Moonlight application: {result.ApplicationName}");
        Console.WriteLine(result.UsesNativeDisplayManagement
            ? $"Virtual-display integration: Sunshine native lifecycle ({result.CoveredApplicationCount} application(s))"
            : $"Virtual-display integration: prep hook ({result.CoveredApplicationCount} application(s))");
        Console.WriteLine($"Force SDR: {(settings.ForceSdr ? "enabled" : "disabled")}");
        Console.WriteLine($"Original apps backup: {result.BackupPath}");
        Console.WriteLine("Restart the streaming host, then select the configured application on the Vita.");
        return ExitSuccess;
    }

    private static int DisplayCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";
        var displays = new DisplayTopologyService();
        if (action == "disable-virtual")
        {
            EnsureAdministrator("Disabling the idle virtual display");
            Console.WriteLine(displays.DisableManagedVirtualDisplays()
                ? "The idle Vita virtual display was disabled. Your physical display layout remains active."
                : "No active Vita virtual display needed to be disabled.");
            return ExitSuccess;
        }
        if (action != "list")
        {
            return InvalidCommand($"display {action}");
        }

        foreach (var display in displays.ListDisplays())
        {
            Console.WriteLine($"[{display.PathIndex}] {(display.IsActive ? "active" : "inactive"),-8} {(display.IsAvailable ? "available" : "unavailable"),-11} {display.FriendlyName}");
            Console.WriteLine($"    {display.DevicePath}");
        }
        return ExitSuccess;
    }

    private static int HostCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var hostMode = GetOption(args, "--host") ?? HostSettings.Load().HostMode;
        if (!hostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Automatic host restart currently supports Sunshine. Restart Apollo from its tray icon.");
        }

        switch (action)
        {
            case "restart":
                EnsureAdministrator("Restarting Sunshine");
                WindowsServiceManager.Restart("SunshineService", "Sunshine");
                Console.WriteLine("Sunshine restarted. It will now detect the installed ViGEmBus driver and the updated Vita configuration.");
                return ExitSuccess;
            case "status":
                var state = WindowsServiceManager.GetState("SunshineService");
                Console.WriteLine($"Sunshine service: {state}");
                return state == WindowsServiceState.Running ? ExitSuccess : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"host {action}");
        }
    }

    private static int DriverCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Virtual display driver setup");
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "install";
        var configuredPath = GetOption(args, "--driver-bundle") ?? GetOption(args, "--displaywizard") ?? HostSettings.Load().DisplayWizardPath;
        var wizard = DisplayWizardAdapter.Locate(configuredPath);

        switch (action)
        {
            case "install":
                wizard.InstallDriver();
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (new DisplayTopologyService().DisableManagedVirtualDisplays()) break;
                    Thread.Sleep(250);
                }
                Console.WriteLine("Virtual display driver installed. A reboot may be required before it appears.");
                return ExitSuccess;
            case "reload":
                wizard.ReloadDriver();
                Console.WriteLine("Virtual display driver reloaded.");
                return ExitSuccess;
            default:
                return InvalidCommand($"driver {action}");
        }
    }

    private static int SessionCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        var manager = new SessionManager();
        switch (action)
        {
            case "start":
                var result = manager.Start(
                    GetRequiredInt(args, "--width"),
                    GetRequiredInt(args, "--height"),
                    GetRequiredInt(args, "--fps"));
                Console.WriteLine($"Streaming display active: {result.DisplayName} at {result.Width}x{result.Height}@{result.Fps}");
                return ExitSuccess;
            case "test":
                var seconds = GetOptionalInt(args, "--seconds", 15, 5, 120);
                try
                {
                    var testResult = manager.Start(
                        GetRequiredInt(args, "--width"),
                        GetRequiredInt(args, "--height"),
                        GetRequiredInt(args, "--fps"));
                    Console.WriteLine($"Test display active: {testResult.DisplayName} at {testResult.Width}x{testResult.Height}@{testResult.Fps}");
                    Console.WriteLine($"Restoring the original display layout in {seconds} seconds...");
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                }
                finally
                {
                    manager.RestoreIfPending();
                }
                Console.WriteLine("Display test completed and the original topology was restored.");
                return ExitSuccess;
            case "stop":
            case "recover":
                Console.WriteLine(manager.RestoreIfPending()
                    ? "Original display topology restored."
                    : "No pending display recovery was found.");
                return ExitSuccess;
            case "status":
                Console.WriteLine(manager.HasPendingRecovery
                    ? $"A streaming display session is active or interrupted. Recovery: {HostStatePaths.RecoveryFile}"
                    : "No display transaction is pending.");
                return ExitSuccess;
            default:
                return InvalidCommand($"session {action}");
        }
    }

    private static int RecoveryCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "install":
                EnsureAdministrator("Installing the automatic display-recovery safeguard");
                RecoveryTaskManager.Install(Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe"));
                Console.WriteLine("The automatic logon recovery task is installed.");
                return ExitSuccess;
            case "uninstall":
                EnsureAdministrator("Removing the automatic display-recovery safeguard");
                RecoveryTaskManager.Uninstall();
                Console.WriteLine("The automatic logon recovery task was removed.");
                return ExitSuccess;
            case "status":
                Console.WriteLine(RecoveryTaskManager.IsInstalled()
                    ? "The automatic logon recovery task is installed."
                    : "The automatic logon recovery task is not installed.");
                return RecoveryTaskManager.IsInstalled() ? ExitSuccess : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"recovery {action}");
        }
    }

    private static int AgentCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "run":
                return HostRecoveryAgentManager.Run(HasFlag(args, "--background"));
            case "install":
                EnsureAdministrator("Installing the stream rescue agent");
                HostRecoveryAgentManager.Install(Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe"));
                Console.WriteLine("The stream rescue agent is installed and running.");
                return ExitSuccess;
            case "uninstall":
                EnsureAdministrator("Removing the stream rescue agent");
                HostRecoveryAgentManager.Uninstall();
                Console.WriteLine("The stream rescue agent was removed.");
                return ExitSuccess;
            case "status":
                var installed = HostRecoveryAgentManager.IsInstalled();
                var running = HostRecoveryAgentManager.IsRunning();
                Console.WriteLine($"Stream rescue task: {(installed ? "installed" : "not installed")}");
                Console.WriteLine($"Stream rescue agent: {(running ? "running" : "not running")}");
                var last = HostRecoveryAgentManager.ReadLastStatus();
                if (last is not null)
                {
                    Console.WriteLine($"Last rescue: {last.Timestamp.LocalDateTime:g} {last.Action} {(last.Success ? "succeeded" : "failed")} - {last.Message}");
                }
                return installed && running ? ExitSuccess : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"agent {action}");
        }
    }

    private static int EmergencyCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Emergency host recovery");
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "recover-display";
        HostRescueStatus result;
        switch (action)
        {
            case "close-foreground":
                result = HostRecoveryActions.CloseForegroundApplication();
                break;
            case "recover-display":
                result = HostRecoveryActions.RecoverDisplayAndStreamingHost();
                break;
            default:
                return InvalidCommand($"emergency {action}");
        }
        Console.WriteLine(result.Message);
        return result.Success ? ExitSuccess : ExitFailure;
    }

    private static int RunDoctor(bool json)
    {
        var report = HostDiagnostics.Inspect();
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            Console.WriteLine("Vita Moonlight host diagnostics");
            Console.WriteLine($"Windows:       {Status(report.IsWindows, report.OperatingSystem)}");
            Console.WriteLine($"Administrator: {Status(report.IsAdministrator, report.IsAdministrator ? "yes" : "no (required for setup)")}");
            Console.WriteLine($"Host mode:     {report.HostMode}");
            Console.WriteLine($"App coverage:  {(report.IntegrateAllSunshineApps ? "every Sunshine app" : "Vita Moonlight app only")}");
            Console.WriteLine($"Display lifecycle: {Status(report.NativeDisplayLifecycleReady, report.NativeDisplayLifecycleReady ? "native disconnect recovery enabled" : "reapply recommended setup")}");
            Console.WriteLine($"Color mode:    {(report.ForceSdr ? "force SDR for Vita sessions" : "leave Windows color mode unchanged")}");
            Console.WriteLine($"Sunshine:      {Status(report.SunshinePath is not null, report.SunshinePath ?? "not found")}");
            Console.WriteLine($"Apollo:        {Status(report.ApolloPath is not null, report.ApolloPath ?? "not found")}");
            Console.WriteLine($"ViGEmBus:      {Status(report.ViGEmBusInstalled, report.ViGEmBusInstalled ? "installed" : "not detected")}");
            Console.WriteLine($"ViGEmBus state:{Status(report.ViGEmBusRunning, report.ViGEmBusRunning ? "running" : report.ViGEmBusInstalled ? "installed but not running" : "unavailable")}");
            Console.WriteLine($"Sunshine gamepad: {Status(!report.SunshineNeedsRestart, report.SunshineNeedsRestart ? "restart required - startup did not see ViGEmBus" : "ready")}");
            Console.WriteLine($"Driver bundle: {Status(report.DisplayWizardPath is not null, report.DisplayWizardPath ?? "not found (not needed for Apollo)")}");
            Console.WriteLine($"Virtual display: {Status(report.VirtualDisplayDriverInstalled || report.HostMode == "apollo", report.HostMode == "apollo" ? "provided by Apollo" : report.VirtualDisplayDriverInstalled ? "signed driver installed" : "not detected")}");
            Console.WriteLine($"Recovery:      {Status(!report.RecoveryPending, report.RecoveryPending ? "pending - run session recover" : "none")}");
            Console.WriteLine($"Recovery task: {Status(report.RecoveryTaskInstalled, report.RecoveryTaskInstalled ? "installed" : "not installed")}");
            Console.WriteLine($"Stream rescue: {Status(report.RescueAgentInstalled && report.RescueAgentRunning, report.RescueAgentRunning ? "installed and running" : report.RescueAgentInstalled ? "installed but not running" : "not installed")}");
            Console.WriteLine();
            Console.WriteLine(report.Recommendation);
        }

        return report.IsWindows && report.HasSelectedStreamingHost && report.HasDisplaySupport && report.NativeDisplayLifecycleReady &&
            report.ViGEmBusInstalled && report.ViGEmBusRunning && !report.SunshineNeedsRestart && !report.RecoveryPending &&
            report.RescueAgentInstalled && report.RescueAgentRunning
            ? ExitSuccess
            : ExitMissingRequiredComponent;
    }

    private static int PrintProfile(bool json)
    {
        var profile = VitaHostProfile.Recommended;
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(profile, JsonOptions));
        }
        else
        {
            Console.WriteLine("Recommended Vita streaming profile");
            Console.WriteLine($"Resolution: {profile.Width}x{profile.Height}");
            Console.WriteLine($"FPS:        {profile.FramesPerSecond}");
            Console.WriteLine($"Bitrate:    {profile.BitrateKbps} Kbps");
            Console.WriteLine($"Gamepad:    {profile.SunshineGamepadMode}");
            Console.WriteLine($"Motion:     {profile.SunshineMotionAsDs4}");
        }
        return ExitSuccess;
    }

    private static int RunSelfTest()
    {
        var profile = VitaHostProfile.Recommended;
        Require(profile.Width == 960 && profile.Height == 544 && profile.BitrateKbps == 8000, "Recommended profile invariant failed.");
        Require(Marshal.SizeOf<DisplayPathInfo>() == 72, "DISPLAYCONFIG_PATH_INFO layout is invalid.");
        Require(Marshal.SizeOf<DisplayModeInfo>() == 64, "DISPLAYCONFIG_MODE_INFO layout is invalid.");
        Require(Marshal.SizeOf<DisplayTargetName>() == 420, "DISPLAYCONFIG_TARGET_DEVICE_NAME layout is invalid.");
        Require(Marshal.SizeOf<DisplaySourceName>() == 84, "DISPLAYCONFIG_SOURCE_DEVICE_NAME layout is invalid.");
        Require(Marshal.SizeOf<DeviceMode>() == 220, "DEVMODE layout is invalid.");

        var sample = new[] { new DisplayPathInfo { Flags = 123, SourceInfo = new DisplayPathSourceInfo { Id = 7 } } };
        var roundTrip = WindowsDisplayNative.BytesToStructures<DisplayPathInfo>(WindowsDisplayNative.StructuresToBytes(sample), 1);
        Require(roundTrip[0].Flags == 123 && roundTrip[0].SourceInfo.Id == 7, "Display topology serialization failed.");
        var command = SunshineConfigurator.BuildStartCommand(@"C:\Program Files\Vita Moonlight\VitaMoonlight.Host.exe");
        Require(command.Contains("%SUNSHINE_CLIENT_WIDTH%", StringComparison.Ordinal), "Sunshine hook generation failed.");

        var sunshineTestDirectory = Path.Combine(Path.GetTempPath(), $"vita-moonlight-self-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sunshineTestDirectory);
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "apps.json"), """
                {
                  "apps": [
                    { "name": "Desktop", "prep-cmd": [] },
                    { "name": "Steam Big Picture", "prep-cmd": [
                      { "do": "steam://open/bigpicture", "undo": "", "elevated": false }
                    ] }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "sunshine.log"), """
                [test]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{11111111-2222-3333-4444-555555555555}",
                    "display_name": "",
                    "edid": { "manufacturer_id": "MTT", "product_code": "1337" },
                    "friendly_name": "VDD by MTT",
                    "info": null
                  }
                ]
                """);
            var integrationSettings = HostSettings.Default with { SunshineConfigDirectory = sunshineTestDirectory };
            var integrationResult = SunshineConfigurator.Configure(integrationSettings, @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            Require(integrationResult.CoveredApplicationCount == 3, "Every-app Sunshine integration count failed.");
            Require(integrationResult.UsesNativeDisplayManagement, "Sunshine native display management was not selected.");
            using var integratedApps = JsonDocument.Parse(File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")));
            foreach (var app in integratedApps.RootElement.GetProperty("apps").EnumerateArray())
            {
                var hasManagedHook = app.TryGetProperty("prep-cmd", out var prepCommands) && prepCommands.EnumerateArray().Any(prep =>
                    prep.GetProperty("do").GetString()?.Contains("VitaMoonlight.Host", StringComparison.OrdinalIgnoreCase) == true);
                Require(!hasManagedHook,
                    $"Sunshine app '{app.GetProperty("name").GetString()}' retained a legacy virtual-display hook.");
            }
            var steamApp = integratedApps.RootElement.GetProperty("apps").EnumerateArray().First(app =>
                app.GetProperty("name").GetString() == "Steam Big Picture");
            Require(steamApp.GetProperty("prep-cmd").EnumerateArray().Any(prep =>
                prep.GetProperty("do").GetString() == "steam://open/bigpicture"),
                "Existing Sunshine preparation commands were not preserved.");
            var nativeConfiguration = File.ReadAllLines(Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            Require(nativeConfiguration.Contains("output_name = {11111111-2222-3333-4444-555555555555}"),
                "Sunshine virtual-display selection failed.");
            Require(nativeConfiguration.Contains("dd_configuration_option = ensure_only_display"),
                "Sunshine exclusive-display configuration failed.");
            Require(nativeConfiguration.Contains("dd_config_revert_on_disconnect = enabled"),
                "Sunshine disconnect recovery configuration failed.");
            Require(SunshineConfigurator.IsNativeDisplayManagementReady(sunshineTestDirectory),
                "Sunshine native display lifecycle readiness check failed.");
        }
        finally
        {
            if (Directory.Exists(sunshineTestDirectory)) Directory.Delete(sunshineTestDirectory, true);
        }

        var testConfiguration = new List<string> { "gamepad = x360", "unrelated = preserved" };
        SunshineConfigurator.UpdateSunshineConfiguration(testConfiguration);
        Require(testConfiguration.Contains("gamepad = auto"), "Sunshine gamepad configuration failed.");
        Require(testConfiguration.Contains("motion_as_ds4 = enabled"), "Sunshine motion configuration failed.");
        Require(testConfiguration.Contains("native_pen_touch = enabled"), "Sunshine touch configuration failed.");
        Require(testConfiguration.Contains("unrelated = preserved"), "Sunshine configuration preservation failed.");

        const string driverConfiguration = "<vdd_settings><resolutions/><options><HardwareCursor>true</HardwareCursor></options></vdd_settings>";
        var updatedDriverConfiguration = DisplayWizardAdapter.AddModeToConfiguration(driverConfiguration, 960, 544, 60);
        Require(updatedDriverConfiguration.Contains("<width>960</width>", StringComparison.Ordinal), "Virtual display width configuration failed.");
        Require(updatedDriverConfiguration.Contains("<height>544</height>", StringComparison.Ordinal), "Virtual display height configuration failed.");
        Require(updatedDriverConfiguration.Contains("<refresh_rate>60</refresh_rate>", StringComparison.Ordinal), "Virtual display refresh configuration failed.");
        Require(updatedDriverConfiguration.Contains("<HardwareCursor>true</HardwareCursor>", StringComparison.Ordinal), "Virtual display option preservation failed.");
        Require(DisplayTopologyService.IsManagedVirtualDisplay(
            new DisplayDescriptor(0, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", true, true)),
            "Signed virtual display identification failed.");
        Require(!DisplayTopologyService.IsManagedVirtualDisplay(
            new DisplayDescriptor(0, "LG ULTRAGEAR+", @"\\?\DISPLAY#GSM5CDB#1", true, true)),
            "Physical display was incorrectly identified as managed virtual display.");
        Require(HostRecoveryActions.IsProtectedProcessName("explorer"), "Windows shell protection failed.");
        Require(HostRecoveryActions.IsProtectedProcessName("Sunshine"), "Sunshine process protection failed.");
        Require(!HostRecoveryActions.IsProtectedProcessName("DOOMEternalx64vk"), "Game process was incorrectly protected.");

        Console.WriteLine("Host companion self-test passed.");
        return ExitSuccess;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("VitaMoonlight.Host gui");
        Console.WriteLine("VitaMoonlight.Host doctor [--json]");
        Console.WriteLine("VitaMoonlight.Host profile [--json]");
        Console.WriteLine("VitaMoonlight.Host configure [--host sunshine|apollo] [--config-dir PATH] [--driver-bundle PATH] [--display-match TEXT] [--all-apps true|false] [--force-sdr true|false]");
        Console.WriteLine("VitaMoonlight.Host host status|restart [--host sunshine]");
        Console.WriteLine("VitaMoonlight.Host driver install|reload [--driver-bundle PATH]");
        Console.WriteLine("VitaMoonlight.Host display list");
        Console.WriteLine("VitaMoonlight.Host display disable-virtual");
        Console.WriteLine("VitaMoonlight.Host session test --width N --height N --fps N [--seconds 5..120]");
        Console.WriteLine("VitaMoonlight.Host session start --width N --height N --fps N");
        Console.WriteLine("VitaMoonlight.Host session stop|recover|status");
        Console.WriteLine("VitaMoonlight.Host recovery install|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host agent run [--background]|install|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host emergency close-foreground|recover-display");
        Console.WriteLine("VitaMoonlight.Host self-test");
        return ExitSuccess;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"{name} requires a value.");
                }
                return args[index + 1];
            }
        }
        return null;
    }

    private static int GetRequiredInt(string[] args, string name)
    {
        var value = GetOption(args, name);
        if (!int.TryParse(value, out var parsed))
        {
            throw new ArgumentException($"{name} requires an integer value.");
        }
        return parsed;
    }

    private static int GetOptionalInt(string[] args, string name, int defaultValue, int minimum, int maximum)
    {
        var raw = GetOption(args, name);
        if (raw is null) return defaultValue;
        if (!int.TryParse(raw, out var value) || value < minimum || value > maximum)
        {
            throw new ArgumentException($"{name} must be an integer from {minimum} through {maximum}.");
        }
        return value;
    }

    private static bool HasFlag(string[] args, string name) => args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool? GetOptionalBool(string[] args, string name)
    {
        var raw = GetOption(args, name);
        if (raw is null) return null;
        if (bool.TryParse(raw, out var value)) return value;
        throw new ArgumentException($"{name} must be true or false.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This command is only supported on Windows.");
        }
    }

    private static void EnsureAdministrator(string operation)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Administrator detection is only supported on Windows.");
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException($"{operation} requires an Administrator terminal.");
        }
    }

    private static int InvalidCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintHelp();
        return ExitInvalidArguments;
    }

    private static string Status(bool ok, string value) => $"{(ok ? "OK" : "--")}  {value}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}

internal sealed record VitaHostProfile(int Width, int Height, int FramesPerSecond, int BitrateKbps, string SunshineGamepadMode, bool SunshineMotionAsDs4)
{
    public static VitaHostProfile Recommended { get; } = new(960, 544, 60, 8000, "auto", true);
}

internal sealed record HostDiagnosticReport(
    bool IsWindows,
    string OperatingSystem,
    bool IsAdministrator,
    string HostMode,
    string? SunshinePath,
    string? ApolloPath,
    bool ViGEmBusInstalled,
    bool ViGEmBusRunning,
    bool SunshineNeedsRestart,
    string? DisplayWizardPath,
    bool VirtualDisplayDriverInstalled,
    bool RecoveryPending,
    bool RecoveryTaskInstalled,
    bool RescueAgentInstalled,
    bool RescueAgentRunning,
    bool IntegrateAllSunshineApps,
    bool ForceSdr,
    bool NativeDisplayLifecycleReady,
    string Recommendation)
{
    public bool HasStreamingHost => SunshinePath is not null || ApolloPath is not null;
    public bool HasSelectedStreamingHost => HostMode == "apollo" ? ApolloPath is not null : SunshinePath is not null;
    public bool HasDisplaySupport => HostMode == "apollo" || (DisplayWizardPath is not null && VirtualDisplayDriverInstalled);
}

internal static class HostDiagnostics
{
    public static HostDiagnosticReport Inspect()
    {
        var settings = HostSettings.Load();
        var isWindows = OperatingSystem.IsWindows();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var sunshine = FindExecutable(Environment.GetEnvironmentVariable("SUNSHINE_PATH"), Path.Combine(programFiles, "Sunshine", "sunshine.exe"));
        var apollo = FindExecutable(Environment.GetEnvironmentVariable("APOLLO_PATH"), Path.Combine(programFiles, "Apollo", "apollo.exe"), Path.Combine(programFiles, "Apollo", "sunshine.exe"));
        string? displayWizard = null;
        try
        {
            var wizard = DisplayWizardAdapter.Locate(settings.DisplayWizardPath);
            wizard.ValidateDriverBundle();
            displayWizard = wizard.ExecutablePath;
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException) { }
        var vigemState = WindowsServiceManager.GetState("ViGEmBus");
        var vigem = vigemState != WindowsServiceState.NotInstalled;
        var vigemRunning = vigemState == WindowsServiceState.Running;
        var sunshineNeedsRestart = settings.HostMode == "sunshine" && vigemRunning && SunshineLogReportsMissingViGEm(settings);
        var virtualDisplay = DisplayWizardAdapter.IsDriverInstalled();
        var recoveryPending = File.Exists(HostStatePaths.RecoveryFile);
        var recoveryTaskInstalled = RecoveryTaskManager.IsInstalled();
        var rescueAgentInstalled = HostRecoveryAgentManager.IsInstalled();
        var rescueAgentRunning = HostRecoveryAgentManager.IsRunning();
        var nativeDisplayLifecycleReady = settings.HostMode != "sunshine" || !settings.IntegrateAllSunshineApps ||
            SunshineConfigurator.IsNativeDisplayManagementReady(
                SunshineConfigurator.ResolveConfigurationDirectory(settings.SunshineConfigDirectory, "sunshine"));

        var recommendation = !isWindows
            ? "Run this companion on the Windows streaming host."
            : recoveryPending
                ? "An earlier session did not restore its display topology. Run `session recover` before streaming."
                : settings.HostMode == "apollo" && apollo is null
                    ? "Install Apollo or configure Sunshine mode, then run this check again."
                    : settings.HostMode == "sunshine" && sunshine is null
                        ? "Install Sunshine or configure Apollo mode, then run this check again."
                    : !vigem
                        ? "Install ViGEmBus from the host's troubleshooting page, reboot, and run this check again."
                        : !vigemRunning
                            ? "ViGEmBus is installed but is not running. Reboot Windows; if it remains stopped, repair the ViGEmBus installation."
                            : sunshineNeedsRestart
                                ? "ViGEmBus is running, but Sunshine started before it was available. Click Restart Sunshine in the control panel."
                        : !nativeDisplayLifecycleReady
                            ? "Sunshine is not configured to restore displays when the Vita disconnects. Click Apply recommended setup."
                        : settings.HostMode == "sunshine" && displayWizard is null
                            ? "Reinstall the host companion's signed display-driver bundle or switch to Apollo."
                            : settings.HostMode == "sunshine" && !virtualDisplay
                                ? "Install the signed virtual display driver, reboot if requested, and run this check again."
                                : !recoveryTaskInstalled
                                    ? "The host is ready, but automatic logon recovery is not installed. In the Administrator control panel, click Install recovery safeguard."
                                : !rescueAgentInstalled || !rescueAgentRunning
                                    ? "Install or repair the stream rescue agent from Help & recovery so the Vita can close a hung game or recover the host display."
                                : "The host is ready. Click Apply recommended setup after an install or update, then launch any Sunshine application from the Vita.";

        return new HostDiagnosticReport(
            isWindows,
            Environment.OSVersion.VersionString,
            IsAdministrator(),
            settings.HostMode,
            sunshine,
            apollo,
            vigem,
            vigemRunning,
            sunshineNeedsRestart,
            displayWizard,
            virtualDisplay,
            recoveryPending,
            recoveryTaskInstalled,
            rescueAgentInstalled,
            rescueAgentRunning,
            settings.IntegrateAllSunshineApps,
            settings.ForceSdr,
            nativeDisplayLifecycleReady,
            recommendation);
    }

    private static string? FindExecutable(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));

    private static bool SunshineLogReportsMissingViGEm(HostSettings settings)
    {
        var configDirectory = SunshineConfigurator.ResolveConfigurationDirectory(settings.SunshineConfigDirectory, "sunshine");
        var logPath = Path.Combine(configDirectory, "sunshine.log");
        if (!File.Exists(logPath)) return false;
        try
        {
            return File.ReadLines(logPath).Any(line => line.Contains(
                "Fatal: ViGEmBus is not installed or running",
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
