using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VitaMoonlight.Host;

internal static class Program
{
    private const int ExitSuccess = 0;
    private const int ExitFailure = 1;
    private const int ExitInvalidArguments = 2;
    private const int ExitMissingRequiredComponent = 3;
    private const int ExitRestartRequired = 4;

    [STAThread]
    public static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "gui";
        var remaining = args.Skip(1).ToArray();
        if (command != "self-test")
        {
            ClearOperationalPathOverrides();
        }
        TryClearLastCommandError();
        try
        {
            return command switch
            {
                "gui" => RunControlPanel(),
                "doctor" => RunDoctor(HasFlag(remaining, "--json")),
                "profile" => PrintProfile(HasFlag(remaining, "--json")),
                "configure" => Configure(remaining),
                "host" => HostCommand(remaining),
                "gamepad" => GamepadCommand(remaining),
                "runtime" => RuntimeCommand(remaining),
                "dependency" => DependencyCommand(remaining),
                "state" => StateCommand(remaining),
                "driver" => DriverCommand(remaining),
                "display" => DisplayCommand(remaining),
                "session" => SessionCommand(remaining),
                "recovery" => RecoveryCommand(remaining),
                "agent" => AgentCommand(remaining),
                "emergency" => EmergencyCommand(remaining),
                "uninstall" => UninstallCommand(remaining),
                "self-test" => RunSelfTest(),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => InvalidCommand(command),
            };
        }
        catch (HostRestartRequiredException error)
        {
            Console.Error.WriteLine($"Restart required: {error.Message}");
            return ExitRestartRequired;
        }
        catch (Exception error)
        {
            var errorDetails = FormatErrorDetails(error);
            Console.Error.WriteLine($"Error: {errorDetails}");
            TryWriteLastCommandError(errorDetails);
            if (Environment.GetEnvironmentVariable("VITA_MOONLIGHT_DEBUG") == "1")
            {
                Console.Error.WriteLine(error);
            }
            return ExitFailure;
        }
    }

    private static void ClearOperationalPathOverrides()
    {
        // Environment overrides exist solely to support isolated self-tests.
        // Scheduled tasks and elevated setup/uninstall commands can inherit a
        // standard user's environment, so operational commands must discover
        // installed components from trusted service/Program Files locations
        // or consume an explicit CLI option.
        foreach (var variable in new[]
                 {
                     "VITA_MOONLIGHT_STATE_DIR",
                     "SUNSHINE_PATH",
                     "SUNSHINE_CONFIG_DIR",
                     "APOLLO_PATH",
                     "APOLLO_CONFIG_DIR",
                     "DISPLAYWIZARD_PATH",
                 })
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static int RunControlPanel()
    {
        var platform = WindowsPlatformCompatibility.Inspect();
        if (!platform.IsSupported)
        {
            MessageBox.Show(
                platform.RequirementMessage,
                "Unsupported Windows platform",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return ExitMissingRequiredComponent;
        }
        EnsureWindows();
        HostControlPanel.Run();
        return ExitSuccess;
    }

    private static int Configure(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Configuring the streaming host");
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
            DisplayWizardPath = previous.DisplayWizardPath,
            DisplayMatch = GetOption(args, "--display-match") ?? previous.DisplayMatch,
            IntegrateAllSunshineApps = GetOptionalBool(args, "--all-apps") ?? previous.IntegrateAllSunshineApps,
            ForceSdr = GetOptionalBool(args, "--force-sdr") ?? previous.ForceSdr,
        };

        var verifyNativeMode = false;
        if (settings.HostMode == "sunshine")
        {
            var sunshine = SunshineCompatibility.Inspect();
            if (!sunshine.IsInstalled)
            {
                throw new InvalidOperationException(
                    "Sunshine is not installed. Run the host installer with Sunshine selected.");
            }
            if (!sunshine.IsSupported)
            {
                throw new InvalidOperationException(
                    $"Sunshine {SunshineCompatibility.MinimumVersionText} or newer is required for safe display switching. " +
                    $"Detected: {sunshine.DetectedVersion ?? "unknown"}. Run the host installer again to update Sunshine.");
            }

            var wizard = DisplayWizardAdapter.LocateBundled();
            wizard.ValidateDriverBundle();
            if (!DisplayWizardAdapter.IsDriverInstalled())
            {
                throw new InvalidOperationException(
                    "The signed virtual display driver is not installed. " +
                    "Click Apply recommended setup or Install/update display driver first.");
            }

            var modesChanged = wizard.EnsureVitaCompatibilityModes();
            var verificationCurrent = DriverNativeModeVerification.IsCurrent(out _);
            if (RequiresNativeModeVerification(modesChanged, verificationCurrent))
            {
                wizard.ReloadDriver();
                verifyNativeMode = true;
            }
            settings = settings with { DisplayWizardPath = wizard.ExecutablePath };
        }

        var companionPath = GetOption(args, "--companion") ?? Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe");
        settings.Save();
        if (verifyNativeMode)
        {
            var verificationResult = PrimeDriverOrReport("prepared");
            if (verificationResult != ExitSuccess)
            {
                return verificationResult;
            }
        }
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

    private static bool RequiresNativeModeVerification(
        bool modesChanged,
        bool verificationCurrent) =>
        modesChanged || !verificationCurrent;

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
        if (action == "ensure-compatible")
        {
            EnsureAdministrator("Installing or updating Sunshine");
            var installer = GetOption(args, "--installer")
                ?? throw new ArgumentException("host ensure-compatible requires --installer PATH.");
            var result = SunshineCompatibility.EnsureCompatible(installer);
            return result.RestartRequired ? ExitRestartRequired : ExitSuccess;
        }

        var hostMode = GetOption(args, "--host") ?? HostSettings.Load().HostMode;
        if (!hostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Automatic host restart currently supports Sunshine. Restart Apollo from its tray icon.");
        }

        switch (action)
        {
            case "restart":
                EnsureAdministrator("Restarting Sunshine");
                WindowsServiceManager.Restart(StreamingHostLocator.FindSunshineServiceName(), "Sunshine");
                Console.WriteLine("Sunshine restarted. It will now detect the installed ViGEmBus driver and the updated Vita configuration.");
                return ExitSuccess;
            case "status":
                var state = WindowsServiceManager.GetState(StreamingHostLocator.FindSunshineServiceName());
                Console.WriteLine($"Sunshine service: {state}");
                return state == WindowsServiceState.Running ? ExitSuccess : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"host {action}");
        }
    }

    private static int GamepadCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "ensure-compatible":
                EnsureAdministrator("Installing or repairing ViGEmBus");
                var installer = GetOption(args, "--installer")
                    ?? throw new ArgumentException(
                        "gamepad ensure-compatible requires --installer PATH.");
                var result = ViGEmBusCompatibility.EnsureCompatible(installer);
                return result.RestartRequired ? ExitRestartRequired : ExitSuccess;
            case "status":
                var state = WindowsServiceManager.GetState("ViGEmBus");
                Console.WriteLine($"ViGEmBus service: {state}");
                return state == WindowsServiceState.Running
                    ? ExitSuccess
                    : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"gamepad {action}");
        }
    }

    private static int RuntimeCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "ensure-compatible":
                EnsureAdministrator("Installing or repairing the virtual display runtime");
                var installer = GetOption(args, "--installer")
                    ?? throw new ArgumentException(
                        "runtime ensure-compatible requires --installer PATH.");
                var result = VisualCppRuntimeCompatibility.EnsureCompatible(installer);
                return result.RestartRequired ? ExitRestartRequired : ExitSuccess;
            case "status":
                var status = VisualCppRuntimeCompatibility.Inspect();
                Console.WriteLine(status.IsSupported
                    ? $"Microsoft Visual C++ runtime: compatible ({status.DetectedVersion})."
                    : $"Microsoft Visual C++ runtime: missing or older than {VisualCppRuntimeCompatibility.MinimumVersionText}.");
                return status.IsSupported
                    ? ExitSuccess
                    : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"runtime {action}");
        }
    }

    private static int DriverCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "install";
        if (action == "status")
        {
            var ready = IsVirtualDisplayReady(out var readiness);
            Console.WriteLine(readiness);
            return ready ? ExitSuccess : ExitMissingRequiredComponent;
        }

        EnsureAdministrator("Virtual display driver setup");
        var wizard = action == "uninstall"
            ? DisplayWizardAdapter.LocateBundledForUninstall()
            : DisplayWizardAdapter.LocateBundled();

        switch (action)
        {
            case "install":
                var runtimeInstaller =
                    GetOption(args, "--runtime-installer") ??
                    Path.Combine(
                        AppContext.BaseDirectory,
                        "tools",
                        "DisplayWizard",
                        "VC_redist.x64.exe");
                var runtimeResult =
                    VisualCppRuntimeCompatibility.EnsureCompatible(runtimeInstaller);
                if (runtimeResult.RestartRequired)
                {
                    return ExitRestartRequired;
                }
                wizard.InstallDriver();
                return PrimeDriverOrReport("installed");
            case "reload":
                wizard.EnsureVitaCompatibilityModes();
                wizard.ReloadDriver();
                return PrimeDriverOrReport("reloaded");
            case "uninstall":
                var restartRequired = wizard.UninstallDriver();
                Console.WriteLine(restartRequired
                    ? "The Vita virtual display driver was removed. Windows requires a restart to finish cleanup."
                    : "The Vita virtual display driver and its managed configuration were removed.");
                return restartRequired ? ExitRestartRequired : ExitSuccess;
            default:
                return InvalidCommand($"driver {action}");
        }
    }

    private static int DependencyCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Removing a shared Windows dependency");
        var action = args.FirstOrDefault()?.ToLowerInvariant();
        var product = args.Skip(1).FirstOrDefault()?.ToLowerInvariant();
        if (action != "uninstall" || product is null)
        {
            return InvalidCommand($"dependency {string.Join(' ', args)}");
        }

        var result = WindowsDependencyUninstaller.Uninstall(product);
        Console.WriteLine(!result.WasInstalled
            ? $"{result.DisplayName} was not installed."
            : result.RestartRequired
                ? $"{result.DisplayName} was removed. Windows requires a restart to finish cleanup."
                : $"{result.DisplayName} was removed.");
        return result.RestartRequired ? ExitRestartRequired : ExitSuccess;
    }

    private static int StateCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Securing Vita Moonlight machine state");
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "secure";
        if (action != "secure")
        {
            return InvalidCommand($"state {action}");
        }
        MachineStateSecurity.Secure();
        Console.WriteLine(
            "Machine settings, recovery records, and host diagnostics are " +
            "protected beneath the Program Files installation.");
        return ExitSuccess;
    }

    private static int PrimeDriverOrReport(string action)
    {
        try
        {
            var mode = new SessionManager().PrimeNativeMode();
            new DisplayTopologyService().DisableManagedVirtualDisplays();
            Console.WriteLine(
                $"Virtual display driver {action} and verified at {mode.Width}x{mode.Height}@{mode.Fps}. " +
                "The idle virtual display is disabled.");
            return ExitSuccess;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            DriverNativeModeVerification.Invalidate();
            throw new InvalidOperationException(
                $"Virtual display driver {action}, but safe native 960x544 verification failed. " +
                $"The host kept or restored the physical display layout and stopped before configuring Sunshine. " +
                $"{error.Message}",
                error);
        }
    }

    private static bool IsVirtualDisplayReady(out string message)
    {
        if (!DisplayWizardAdapter.IsDriverInstalled())
        {
            message = "Virtual display driver: not installed.";
            return false;
        }
        if (!DisplayWizardAdapter.HasVitaCompatibilityModes())
        {
            message = "Virtual display driver: installed, but Vita display modes are incomplete.";
            return false;
        }
        if (!DriverNativeModeVerification.IsCurrent(out var verification))
        {
            message =
                $"Virtual display driver: installed, but {verification}. Run Install/update display driver.";
            return false;
        }
        try
        {
            var display = new DisplayTopologyService().ListDisplays().FirstOrDefault(candidate =>
                candidate.IsAvailable && DisplayTopologyService.IsManagedVirtualDisplay(candidate));
            if (display is null)
            {
                message =
                    "Virtual display driver: installed, but Windows has not enumerated the display. Restart Windows.";
                return false;
            }
            message = $"Virtual display driver: ready ({display.FriendlyName}).";
            return true;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception)
        {
            message = $"Virtual display driver: not ready ({error.Message}).";
            return false;
        }
    }

    private static int SessionCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        if (action != "status")
        {
            EnsureAdministrator(
                "Managing the Vita streaming display session");
        }
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
            case "mode":
                var modeResult = manager.ChangeMode(
                    GetRequiredInt(args, "--width"),
                    GetRequiredInt(args, "--height"),
                    GetOptionalInt(args, "--fps", 60, 60, 60));
                Console.WriteLine(
                    $"Streaming display mode changed: {modeResult.DisplayName} at {modeResult.Mode}");
                return ExitSuccess;
            case "stop":
            case "recover":
                Console.WriteLine(manager.RestoreIfPending()
                    ? "Original display topology restored."
                    : "No pending display recovery was found.");
                return ExitSuccess;
            case "recover-upgrade":
                var safeRecovery =
                    UninstallManager.RecoverPhysicalAndDiscardPendingTransaction();
                Console.WriteLine(
                    $"Confirmed physical-only display topology: " +
                    $"{string.Join(", ", safeRecovery.PhysicalDisplays)}.");
                Console.WriteLine(safeRecovery.ClearedSavedTransaction
                    ? "A legacy display transaction was discarded without applying it."
                    : "No legacy display transaction needed cleanup.");
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
                InstallationTrust.RequireInstalledPayload(
                    "Running the stream rescue agent");
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
                var modeHotkeys = HostRecoveryAgentManager.GetModeHotkeyReadiness();
                Console.WriteLine($"Stream rescue task: {(installed ? "installed" : "not installed")}");
                Console.WriteLine($"Stream rescue agent: {(running ? "running" : "not running")}");
                foreach (var modeHotkey in modeHotkeys)
                {
                    Console.WriteLine(
                        $"Display mode {modeHotkey.Mode}: {(modeHotkey.Ready ? "hotkey ready" : "hotkey unavailable")}");
                }
                var last = HostRecoveryAgentManager.ReadLastStatus();
                if (last is not null)
                {
                    Console.WriteLine($"Last rescue: {last.Timestamp.LocalDateTime:g} {last.Action} {(last.Success ? "succeeded" : "failed")} - {last.Message}");
                }
                return installed && running && modeHotkeys.All(status => status.Ready)
                    ? ExitSuccess
                    : ExitMissingRequiredComponent;
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
            case "reset-display-driver":
                result = HostRecoveryActions.RecoverDisplayAndStreamingHost();
                break;
            default:
                return InvalidCommand($"emergency {action}");
        }
        Console.WriteLine(result.Message);
        return result.Success ? ExitSuccess : ExitFailure;
    }

    private static int UninstallCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Preparing Vita Moonlight Host for uninstall");
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "prepare";
        if (action == "cleanup-integration")
        {
            var cleanup = UninstallManager.CleanupIntegration();
            Console.WriteLine(
                $"Restored Vita-owned Sunshine settings in: {cleanup.ConfigurationDirectory}.");
            Console.WriteLine(
                $"Removed {cleanup.RemovedHooks} exact Vita-owned Sunshine preparation hook(s).");
            return ExitSuccess;
        }
        if (action != "prepare")
        {
            return InvalidCommand($"uninstall {action}");
        }

        var result = UninstallManager.Prepare();
        Console.WriteLine(
            $"Confirmed physical-only display topology: {string.Join(", ", result.PhysicalDisplays)}.");
        Console.WriteLine(result.ClearedSavedTransaction
            ? "A stale display transaction was cleared after physical recovery."
            : "No saved display transaction needed cleanup.");
        Console.WriteLine(
            "The shared MTT virtual display driver was kept. " +
            "Removing it requires an explicit uninstall selection or `driver uninstall`.");
        return ExitSuccess;
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
            Console.WriteLine($"Platform:      {Status(report.IsSupportedPlatform, report.Architecture)}");
            Console.WriteLine($"Administrator: {Status(report.IsAdministrator, report.IsAdministrator ? "yes" : "no (required for setup)")}");
            Console.WriteLine($"Host mode:     {report.HostMode}");
            Console.WriteLine($"App coverage:  {(report.IntegrateAllSunshineApps ? "every Sunshine app" : "Vita Moonlight app only")}");
            Console.WriteLine($"Display lifecycle: {Status(report.NativeDisplayLifecycleReady, report.NativeDisplayLifecycleReady ? "native disconnect recovery enabled" : "reapply recommended setup")}");
            Console.WriteLine($"Color mode:    {(report.ForceSdr ? "force SDR for Vita sessions" : "leave Windows color mode unchanged")}");
            Console.WriteLine($"Sunshine:      {Status(report.SunshinePath is not null, report.SunshinePath ?? "not found")}");
            Console.WriteLine($"Sunshine version: {Status(
                report.HostMode != "sunshine" || report.SunshineVersionSupported,
                report.SunshineVersion is null
                    ? $"not detected (requires {SunshineCompatibility.MinimumVersionText}+)"
                    : $"{report.SunshineVersion} (requires {SunshineCompatibility.MinimumVersionText}+)")}");
            Console.WriteLine($"Apollo:        {Status(report.ApolloPath is not null, report.ApolloPath ?? "not found")}");
            Console.WriteLine($"ViGEmBus:      {Status(report.ViGEmBusInstalled, report.ViGEmBusInstalled ? "installed" : "not detected")}");
            Console.WriteLine($"ViGEmBus state:{Status(report.ViGEmBusRunning, report.ViGEmBusRunning ? "running" : report.ViGEmBusInstalled ? "installed but not running" : "unavailable")}");
            Console.WriteLine($"Sunshine gamepad: {Status(!report.SunshineNeedsRestart, report.SunshineNeedsRestart ? "restart required - startup did not see ViGEmBus" : "ready")}");
            Console.WriteLine($"Driver bundle: {Status(report.DisplayWizardPath is not null, report.DisplayWizardPath ?? "not found (not needed for Apollo)")}");
            Console.WriteLine($"VDD runtime:   {Status(
                report.VisualCppRuntimeSupported || report.HostMode == "apollo",
                report.HostMode == "apollo"
                    ? "not needed for Apollo"
                    : report.VisualCppRuntimeSupported
                        ? $"Microsoft Visual C++ {report.VisualCppRuntimeVersion}"
                        : $"missing or older than {VisualCppRuntimeCompatibility.MinimumVersionText}")}");
            Console.WriteLine($"Virtual display: {Status(report.VirtualDisplayDriverInstalled || report.HostMode == "apollo", report.HostMode == "apollo" ? "provided by Apollo" : report.VirtualDisplayDriverInstalled ? "signed driver installed" : "not detected")}");
            Console.WriteLine($"Vita display modes: {Status(report.VirtualDisplayModesReady || report.HostMode == "apollo", report.HostMode == "apollo" ? "provided by Apollo" : report.VirtualDisplayModesReady ? "provisioned; native 960x544 verified" : "missing or unverified - repair display driver")}");
            Console.WriteLine($"Recovery:      {Status(!report.RecoveryPending, report.RecoveryPending ? "pending - run session recover" : "none")}");
            Console.WriteLine($"Recovery task: {Status(report.RecoveryTaskInstalled, report.RecoveryTaskInstalled ? "installed" : "not installed")}");
            var rescueDescription = report.RescueAgentInstalled && report.RescueAgentRunning
                ? "installed and running"
                : !report.RescueAgentInstalled && report.RescueAgentRunning
                    ? "orphan agent running; scheduled task missing - repair required"
                    : report.RescueAgentInstalled
                        ? "installed but not running"
                        : "not installed";
            Console.WriteLine($"Stream rescue: {Status(report.RescueAgentInstalled && report.RescueAgentRunning, rescueDescription)}");
            var modeHotkeyDescription = string.Join(
                ", ",
                report.ModeHotkeys.Select(status =>
                    $"{status.Mode} {(status.Ready ? "ready" : "unavailable")}"));
            Console.WriteLine($"Display mode controls: {Status(report.ModeHotkeysReady, modeHotkeyDescription)}");
            Console.WriteLine();
            Console.WriteLine(report.Recommendation);
        }

        return report.IsSupportedPlatform && report.HasSelectedStreamingHost &&
            (report.HostMode != "sunshine" || report.SunshineVersionSupported) &&
            report.HasDisplaySupport && report.NativeDisplayLifecycleReady &&
            report.ViGEmBusInstalled && report.ViGEmBusRunning && !report.SunshineNeedsRestart && !report.RecoveryPending &&
            (report.HostMode == "apollo" || report.VisualCppRuntimeSupported) &&
            report.RescueAgentInstalled && report.RescueAgentRunning && report.ModeHotkeysReady
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
        using var installationTrustTest =
            InstallationTrust.AllowSelfTestPaths();
        var profile = VitaHostProfile.Recommended;
        Require(profile.Width == 960 && profile.Height == 544 && profile.BitrateKbps == 8000, "Recommended profile invariant failed.");
        Require(SunshineCompatibility.IsVersionSupported("2026.516.143833"),
            "Pinned Sunshine minimum version was rejected.");
        Require(SunshineCompatibility.IsVersionSupported("2026.527.25539"),
            "A newer Sunshine version was rejected.");
        Require(!SunshineCompatibility.IsVersionSupported("2025.122.141614"),
            "An incompatible Sunshine version was accepted.");
        Require(VisualCppRuntimeCompatibility.IsVersionSupported("v14.44.35211.0"),
            "The pinned Microsoft Visual C++ runtime version was rejected.");
        Require(VisualCppRuntimeCompatibility.IsVersionSupported("14.50.10000.0"),
            "A newer Microsoft Visual C++ runtime version was rejected.");
        Require(!VisualCppRuntimeCompatibility.IsVersionSupported("14.43.99999.0"),
            "An older Microsoft Visual C++ runtime version was accepted.");
        Require(WindowsPlatformCompatibility.IsSupported(
                true,
                true,
                Architecture.X64,
                Architecture.X64,
                "Client"),
            "Supported client Windows x64 was rejected.");
        Require(!WindowsPlatformCompatibility.IsSupported(
                true,
                true,
                Architecture.X64,
                Architecture.X64,
                "Server"),
            "Windows Server was accepted as a supported release target.");
        Require(!WindowsPlatformCompatibility.IsSupported(
                true,
                true,
                Architecture.Arm64,
                Architecture.X64,
                "Client"),
            "Windows ARM64 was accepted as a supported release target.");
        Require(
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(0) == PnPUtilExitDisposition.Success &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(259) == PnPUtilExitDisposition.ContinueToVerification &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(50) == PnPUtilExitDisposition.Failure &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(50, allowAlreadyEnabledNoOp: true) ==
                PnPUtilExitDisposition.ContinueToVerification &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(3010) == PnPUtilExitDisposition.RestartRequired &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(1641) == PnPUtilExitDisposition.RestartRequired &&
            DisplayWizardAdapter.ClassifyPnPUtilExitCode(5) == PnPUtilExitDisposition.Failure,
            "PnPUtil exit-code classification failed.");
        Require(
            WindowsDependencyUninstaller.ClassifyExitCode(0) ==
                DependencyUninstallDisposition.Success &&
            WindowsDependencyUninstaller.ClassifyExitCode(1605) ==
                DependencyUninstallDisposition.Success &&
            WindowsDependencyUninstaller.ClassifyExitCode(1614) ==
                DependencyUninstallDisposition.Success &&
            WindowsDependencyUninstaller.ClassifyExitCode(3010) ==
                DependencyUninstallDisposition.RestartRequired &&
            WindowsDependencyUninstaller.ClassifyExitCode(1618) ==
                DependencyUninstallDisposition.Failure,
            "Windows dependency uninstall exit-code classification failed.");
        var formattedError = FormatErrorDetails(
            new InvalidOperationException(
                "outer setup failure",
                new Win32Exception(50, "already-enabled device no-op")));
        Require(
            formattedError.Contains("outer setup failure", StringComparison.Ordinal) &&
            formattedError.Contains("already-enabled device no-op", StringComparison.Ordinal),
            "Host error breadcrumb formatting lost an inner failure.");
        Require(
            !RequiresNativeModeVerification(modesChanged: false, verificationCurrent: true) &&
            RequiresNativeModeVerification(modesChanged: true, verificationCurrent: true) &&
            RequiresNativeModeVerification(modesChanged: false, verificationCurrent: false),
            "Native display verification gating failed.");
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
            using var ownershipJournalTest = SunshineOwnershipJournal.UseTestFile(
                Path.Combine(sunshineTestDirectory, "ownership-test.json"));
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
            Require(integrationResult.CoveredApplicationCount == 2, "Every-app Sunshine integration count failed.");
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
            Require(nativeConfiguration.Contains("dd_refresh_rate_option = manual"),
                "Sunshine fixed virtual-display refresh configuration failed.");
            Require(nativeConfiguration.Contains("dd_manual_refresh_rate = 60"),
                "Sunshine virtual-display refresh rate failed.");
            Require(nativeConfiguration.Any(line => line.Contains(
                    "{\"final_resolution\":\"960x544\"}", StringComparison.Ordinal)),
                "Sunshine native-resolution fallback failed.");
            Require(nativeConfiguration.Contains("dd_config_revert_on_disconnect = enabled"),
                "Sunshine disconnect recovery configuration failed.");
            Require(SunshineConfigurator.IsNativeDisplayManagementReady(sunshineTestDirectory),
                "Sunshine native display lifecycle readiness check failed.");
            File.WriteAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                nativeConfiguration.Where(line => !line.StartsWith("dd_mode_remapping =", StringComparison.OrdinalIgnoreCase)));
            Require(!SunshineConfigurator.IsNativeDisplayManagementReady(sunshineTestDirectory),
                "Sunshine readiness accepted a missing safe-resolution mapping.");
            File.WriteAllLines(Path.Combine(sunshineTestDirectory, "sunshine.conf"), nativeConfiguration);
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "sunshine-reversed.log"), """
                [test]: Info: Currently available display devices:
                [
                  {
                    "friendly_name": "VDD by MTT",
                    "edid": { "product_code": "1337", "manufacturer_id": "MTT" },
                    "device_id": "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"
                  }
                ]
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-reversed.log"), null) ==
                    "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
                "Sunshine display parsing failed when properties were reordered.");
            Require(SunshineConfigurator.WaitForManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-reversed.log"),
                    null,
                    timeoutMilliseconds: 0,
                    pollMilliseconds: 1) ==
                    "{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}",
                "Sunshine display enumeration polling rejected an existing inventory.");
            Require(SunshineConfigurator.WaitForManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "missing.log"),
                    null,
                    timeoutMilliseconds: 0,
                    pollMilliseconds: 1) is null,
                "Sunshine display enumeration timeout failed.");
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "sunshine-stale.log"), """
                [old]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{11111111-1111-1111-1111-111111111111}",
                    "friendly_name": "VDD by MTT",
                    "edid": { "manufacturer_id": "MTT", "product_code": "1337" }
                  }
                ]
                [new]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{22222222-2222-2222-2222-222222222222}",
                    "friendly_name": "Physical Monitor"
                  }
                ]
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-stale.log"), null) is null,
                "Sunshine display selection accepted a stale virtual display from an older inventory.");
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "sunshine-incomplete.log"), """
                [old]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{33333333-3333-3333-3333-333333333333}",
                    "friendly_name": "VDD by MTT",
                    "edid": { "manufacturer_id": "MTT", "product_code": "1337" }
                  }
                ]
                [new]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{44444444-4444-4444-4444-444444444444}",
                    "friendly_name": "VDD by MTT"
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-incomplete.log"), null) is null,
                "Sunshine display selection fell back while the newest inventory was incomplete.");
            File.WriteAllText(Path.Combine(sunshineTestDirectory, "sunshine-dual-vdd.log"), """
                [new]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{55555555-5555-5555-5555-555555555555}",
                    "friendly_name": "VDD by MTT",
                    "edid": { "manufacturer_id": "MTT", "product_code": "1337" }
                  },
                  {
                    "device_id": "{66666666-6666-6666-6666-666666666666}",
                    "friendly_name": "Apollo Virtual Display"
                  }
                ]
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-dual-vdd.log"), null) ==
                    "{55555555-5555-5555-5555-555555555555}",
                "Default Sunshine display selection did not prefer the managed MTT display.");
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    Path.Combine(sunshineTestDirectory, "sunshine-dual-vdd.log"), "Apollo") ==
                    "{66666666-6666-6666-6666-666666666666}",
                "An explicit Sunshine display match could not select an alternate virtual display.");

            var cleanupResult = SunshineConfigurator.RemoveManagedIntegration();
            Require(!cleanupResult.RemovedGeneratedApplication,
                "Native all-app configuration unexpectedly created a disposable Sunshine application.");
            Require(cleanupResult.RemovedNativeDisplaySettings,
                "Uninstall cleanup retained the managed Sunshine display block.");
            Require(!File.Exists(Path.Combine(
                    sunshineTestDirectory,
                    "apps.json.vita-moonlight.backup")),
                "Uninstall cleanup retained the Vita Sunshine backup.");
            using var cleanedApps = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")));
            Require(!cleanedApps.RootElement.GetProperty("apps").EnumerateArray().Any(app =>
                    app.GetProperty("name").GetString() == "Vita Moonlight"),
                "Native all-app configuration created a redundant Vita application.");
            var cleanedSteam = cleanedApps.RootElement.GetProperty("apps").EnumerateArray().First(app =>
                app.GetProperty("name").GetString() == "Steam Big Picture");
            Require(cleanedSteam.GetProperty("prep-cmd").EnumerateArray().Any(prep =>
                    prep.GetProperty("do").GetString() == "steam://open/bigpicture"),
                "Uninstall cleanup removed a user-owned Sunshine preparation command.");
            var cleanedConfiguration = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            Require(!cleanedConfiguration.Any(line =>
                    line.StartsWith("dd_configuration_option =", StringComparison.OrdinalIgnoreCase)),
                "Uninstall cleanup retained a newly added Sunshine display setting.");
            Require(!cleanedConfiguration.Any(line =>
                    line.StartsWith("controller =", StringComparison.OrdinalIgnoreCase)),
                "Uninstall cleanup retained a newly added Sunshine controller setting.");
            var secondCleanup = SunshineConfigurator.RemoveManagedIntegration();
            Require(
                secondCleanup.RemovedHooks == 0 &&
                !secondCleanup.RemovedGeneratedApplication &&
                !secondCleanup.RemovedNativeDisplaySettings,
                "Sunshine uninstall cleanup was not idempotent.");

            var legacySettings = integrationSettings with
            {
                IntegrateAllSunshineApps = false,
            };
            var preJournalRoot = JsonNode.Parse(
                File.ReadAllText(
                    Path.Combine(
                        sunshineTestDirectory,
                        "apps.json")))!.AsObject();
            preJournalRoot["apps"]!.AsArray().Add(
                new JsonObject
                {
                    ["name"] = "Vita Moonlight",
                    ["cmd"] = string.Empty,
                    ["output"] = string.Empty,
                    ["exclude-global-prep-cmd"] = false,
                    ["prep-cmd"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["do"] =
                                SunshineConfigurator.BuildStartCommand(
                                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                            ["undo"] =
                                SunshineConfigurator.BuildStopCommand(
                                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                            ["elevated"] = true,
                        },
                    },
                });
            File.WriteAllText(
                Path.Combine(sunshineTestDirectory, "apps.json"),
                preJournalRoot.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
            SunshineConfigurator.Configure(
                legacySettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var legacyCleanup = SunshineConfigurator.RemoveManagedIntegration();
            Require(
                legacyCleanup.RemovedHooks == 1 &&
                legacyCleanup.RemovedGeneratedApplication,
                "Exact owned hook/application cleanup failed.");

            SunshineConfigurator.Configure(
                legacySettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var userEditedRoot = JsonNode.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")))!.AsObject();
            var userEditedApp = userEditedRoot["apps"]!.AsArray()
                .OfType<JsonObject>()
                .First(app => app["name"]?.GetValue<string>() == "Vita Moonlight");
            userEditedApp["cmd"] = "user-owned-command";
            File.WriteAllText(
                Path.Combine(sunshineTestDirectory, "apps.json"),
                userEditedRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var editedBackupPath = Path.Combine(
                sunshineTestDirectory,
                "apps.json.vita-moonlight.backup");
            File.AppendAllText(
                editedBackupPath,
                $"{Environment.NewLine}user-preserved-backup-edit");
            var editedCleanup = SunshineConfigurator.RemoveManagedIntegration();
            Require(
                editedCleanup.RemovedHooks == 1 &&
                !editedCleanup.RemovedGeneratedApplication,
                "Cleanup did not preserve a user-modified generated application.");
            using var editedApps = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")));
            var preservedEditedApp = editedApps.RootElement.GetProperty("apps")
                .EnumerateArray()
                .First(app => app.GetProperty("name").GetString() == "Vita Moonlight");
            Require(
                preservedEditedApp.GetProperty("cmd").GetString() == "user-owned-command" &&
                !preservedEditedApp.TryGetProperty("prep-cmd", out _),
                "Cleanup did not remove only the exact owned hook from a user-modified app.");
            Require(
                File.Exists(editedBackupPath),
                "Cleanup deleted a backup whose owned fingerprint changed.");
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
        var tamperedConfiguration = new List<string>
        {
            "output_name = user-changed-display",
            "controller = enabled",
            "new-setting = managed",
            "unrelated = preserved",
        };
        var ownership = new SunshineOwnedLocation
        {
            ConfigurationDirectory = sunshineTestDirectory,
            Values = new Dictionary<string, SunshineOwnedValue>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["output_name"] = new(true, "original-display", "managed-display"),
                ["controller"] = new(true, "disabled", "enabled"),
                ["new-setting"] = new(false, null, "managed"),
            },
        };
        Require(
            SunshineConfigurator.RestoreOwnedConfiguration(
                tamperedConfiguration,
                ownership) &&
            tamperedConfiguration.Contains("output_name = user-changed-display") &&
            tamperedConfiguration.Contains("controller = disabled") &&
            !tamperedConfiguration.Any(line =>
                line.StartsWith("new-setting =", StringComparison.OrdinalIgnoreCase)) &&
            tamperedConfiguration.Contains("unrelated = preserved"),
            "Ownership cleanup did not preserve tampered values and exactly restore current managed values.");

        const string driverConfiguration = "<vdd_settings><resolutions/><options><HardwareCursor>true</HardwareCursor></options></vdd_settings>";
        var updatedDriverConfiguration = DisplayWizardAdapter.AddModeToConfiguration(driverConfiguration, 960, 544, 60);
        Require(updatedDriverConfiguration.Contains("<width>960</width>", StringComparison.Ordinal), "Virtual display width configuration failed.");
        Require(updatedDriverConfiguration.Contains("<height>544</height>", StringComparison.Ordinal), "Virtual display height configuration failed.");
        Require(updatedDriverConfiguration.Contains("<refresh_rate>60</refresh_rate>", StringComparison.Ordinal), "Virtual display refresh configuration failed.");
        Require(updatedDriverConfiguration.Contains("<HardwareCursor>true</HardwareCursor>", StringComparison.Ordinal), "Virtual display option preservation failed.");
        var compatibleDriverConfiguration = DisplayWizardAdapter.AddVitaCompatibilityModesToConfiguration(driverConfiguration);
        Require(DisplayWizardAdapter.HasVitaCompatibilityModesInConfiguration(compatibleDriverConfiguration),
            "Vita virtual display mode provisioning failed.");
        var compatibleDriverDocument = System.Xml.Linq.XDocument.Parse(compatibleDriverConfiguration);
        var firstDriverMode = compatibleDriverDocument.Root?.Element("resolutions")?.Elements("resolution").First();
        Require(
            firstDriverMode?.Element("width")?.Value == "960" &&
            firstDriverMode.Element("height")?.Value == "544",
            "Vita native mode is not the virtual driver's preferred mode.");
        Require(compatibleDriverConfiguration.Contains("<height>540</height>", StringComparison.Ordinal),
            "Vita 960x540 compatibility mode was not provisioned.");
        Require(!compatibleDriverConfiguration.Contains("<height>576</height>", StringComparison.Ordinal),
            "Vita mode provisioning added an unsafe nonessential mode.");
        Require(DisplayWizardAdapter.AddVitaCompatibilityModesToConfiguration(compatibleDriverConfiguration) == compatibleDriverConfiguration,
            "Vita virtual display mode provisioning was not idempotent.");
        const string existingRepairConfiguration = """
            <vdd_settings>
              <global>
                <g_refresh_rate>60</g_refresh_rate>
                <g_refresh_rate>60</g_refresh_rate>
                <g_refresh_rate>120</g_refresh_rate>
              </global>
              <resolutions>
                <resolution>
                  <width>960</width>
                  <height>544</height>
                  <refresh_rate>60</refresh_rate>
                  <refresh_rate>60</refresh_rate>
                </resolution>
              </resolutions>
              <options><HardwareCursor>true</HardwareCursor></options>
            </vdd_settings>
            """;
        var normalizedRepairConfiguration =
            DisplayWizardAdapter.AddVitaCompatibilityModesToConfiguration(existingRepairConfiguration);
        var normalizedRepairDocument = System.Xml.Linq.XDocument.Parse(normalizedRepairConfiguration);
        Require(
            normalizedRepairDocument.Root?.Element("global")?.Elements("g_refresh_rate")
                .Count(rate => rate.Value == "60") == 1 &&
            normalizedRepairDocument.Root.Element("resolutions")?.Elements("resolution")
                .First(mode => mode.Element("width")?.Value == "960" && mode.Element("height")?.Value == "544")
                .Elements("refresh_rate")
                .All(rate => rate.Value != "60") == true &&
            normalizedRepairConfiguration.Contains("<HardwareCursor>true</HardwareCursor>", StringComparison.Ordinal),
            "Existing-driver repair did not remove duplicate effective modes while preserving options.");
        Require(
            DisplayWizardAdapter.HasVitaCompatibilityModesInConfiguration(normalizedRepairConfiguration),
            "Global 60 Hz was not recognized as an effective Vita compatibility refresh rate.");
        var driverConfigurationSha256 = DriverNativeModeVerification.ComputeConfigurationSha256(
            System.Text.Encoding.UTF8.GetBytes(compatibleDriverConfiguration));
        var changedDriverConfigurationSha256 = DriverNativeModeVerification.ComputeConfigurationSha256(
            System.Text.Encoding.UTF8.GetBytes(compatibleDriverConfiguration + Environment.NewLine));
        var nativeModeVerification = DriverNativeModeVerification.CreateRecord(
            driverConfigurationSha256,
            DateTimeOffset.UnixEpoch);
        Require(DriverNativeModeVerification.Matches(nativeModeVerification, driverConfigurationSha256),
            "A current native-mode verification record was rejected.");
        Require(!DriverNativeModeVerification.Matches(nativeModeVerification, changedDriverConfigurationSha256),
            "A native-mode verification record survived a display-driver configuration change.");
        Require(!DriverNativeModeVerification.Matches(
                nativeModeVerification with { Height = 540 },
                driverConfigurationSha256),
            "A non-native mode was accepted as a native-mode verification.");
        Require(!DriverNativeModeVerification.Matches(
                nativeModeVerification with { ConfigurationSha256 = "invalid" },
                driverConfigurationSha256),
            "A malformed native-mode verification fingerprint was accepted.");
        var protectedDriverDirectoryIdentity =
            new TrustedFileIdentity(
                VolumeSerialNumber: 0x12345678,
                FileIndexHigh: 0x90ABCDEF,
                FileIndexLow: 0x10203040);
        var protectedDriverDirectoryRecord =
            DriverConfigurationDirectoryTrust.CreateRecord(
                protectedDriverDirectoryIdentity);
        Require(
            DriverConfigurationDirectoryTrust.Matches(
                protectedDriverDirectoryRecord,
                protectedDriverDirectoryIdentity),
            "An exact protected display-driver directory identity was rejected.");
        Require(
            !DriverConfigurationDirectoryTrust.Matches(
                protectedDriverDirectoryRecord,
                protectedDriverDirectoryIdentity with
                {
                    FileIndexLow =
                        protectedDriverDirectoryIdentity.FileIndexLow + 1,
                }) &&
            !DriverConfigurationDirectoryTrust.Matches(
                protectedDriverDirectoryRecord with { FormatVersion = 0 },
                protectedDriverDirectoryIdentity),
            "A replaced display-driver directory or stale identity record was accepted.");
        Require(VitaDisplayModes.RequireSupported(960, 544, 60) == VitaDisplayModes.Native,
            "Vita native runtime mode validation failed.");
        try
        {
            VitaDisplayModes.RequireSupported(800, 600, 60);
            throw new InvalidOperationException("Unsafe runtime virtual-display mode was accepted.");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Expected: runtime control is deliberately restricted to provisioned Vita modes.
        }
        Require(
            HostRecoveryHotkeyWindow.ModeForHotkeyId(4) == VitaDisplayModes.Native &&
            HostRecoveryHotkeyWindow.ModeForHotkeyId(99) is null,
            "In-stream native display-mode hotkey mapping failed.");
        Require(
            VitaDisplayModes.Supported
                .Select(HostRecoveryAgentManager.ModeHotkeyReadyEventName)
                .Distinct(StringComparer.Ordinal)
                .Count() == VitaDisplayModes.Supported.Count,
            "Display-mode hotkey readiness events are not unique.");
        Require(DisplayTopologyService.IsManagedVirtualDisplay(
            new DisplayDescriptor(0, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", true, true)),
            "Signed virtual display identification failed.");
        Require(!DisplayTopologyService.IsManagedVirtualDisplay(
            new DisplayDescriptor(0, "Physical Monitor", @"\\?\DISPLAY#ACME123#1", true, true)),
            "Physical display was incorrectly identified as managed virtual display.");
        var safeVirtualSelection = DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
        {
            new DisplayDescriptor(0, "MTT Office Monitor", @"\\?\DISPLAY#PHYSICAL#1", true, true),
            new DisplayDescriptor(1, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", false, true),
        }, "MTT");
        Require(safeVirtualSelection?.FriendlyName == "VDD by MTT",
            "A configured virtual-display match was allowed to select a physical monitor.");
        var verificationDisplays = DisplayTopologyService.SelectActivePhysicalDisplaysForVerification(new[]
        {
            new DisplayDescriptor(0, "Internal Panel", @"\\?\DISPLAY#INTERNAL#1", true, true),
            new DisplayDescriptor(1, "External Monitor", @"\\?\DISPLAY#EXTERNAL#1", true, true),
            new DisplayDescriptor(2, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", true, true),
            new DisplayDescriptor(3, "Dock Monitor", @"\\?\DISPLAY#DOCK#1", false, true),
        });
        Require(
            verificationDisplays.Length == 2 &&
            verificationDisplays.All(display => display.IsActive && !DisplayTopologyService.IsLikelyVirtualDisplay(display)),
            "Safe native-mode verification did not preserve every active physical display.");
        var recoveryDisplays = DisplayTopologyService.SelectPhysicalDisplaysForRecovery(new[]
        {
            new DisplayDescriptor(0, "Internal Panel", @"\\?\DISPLAY#INTERNAL#1", false, true),
            new DisplayDescriptor(1, "External Monitor", @"\\?\DISPLAY#EXTERNAL#1", false, true),
            new DisplayDescriptor(2, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", true, true),
        });
        Require(recoveryDisplays.Length == 2 && recoveryDisplays.All(display => !DisplayTopologyService.IsManagedVirtualDisplay(display)),
            "Multi-monitor physical display recovery selection failed.");
        var apolloRecoveryDisplays = DisplayTopologyService.SelectPhysicalDisplaysForRecovery(new[]
        {
            new DisplayDescriptor(0, "Apollo Virtual Display", @"\\?\DISPLAY#APOLLO#1", true, true),
            new DisplayDescriptor(1, "Internal Panel", @"\\?\DISPLAY#INTERNAL#1", false, true),
        });
        Require(
            DisplayTopologyService.IsLikelyVirtualDisplay(
                new DisplayDescriptor(0, "Apollo Virtual Display", @"\\?\DISPLAY#APOLLO#1", true, true)) &&
            apolloRecoveryDisplays.Length == 1 &&
            apolloRecoveryDisplays[0].FriendlyName == "Internal Panel",
            "Emergency recovery treated an active Apollo virtual display as a physical monitor.");
        var localizedIndirectRecoveryDisplays =
            DisplayTopologyService.SelectPhysicalDisplaysForRecovery(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Pantalla desconocida",
                    @"\\?\DISPLAY#UNKNOWN#1",
                    true,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectWired),
                new DisplayDescriptor(
                    1,
                    "Panel interno",
                    @"\\?\DISPLAY#PHYSICAL#1",
                    false,
                    true,
                    5),
            });
        Require(
            localizedIndirectRecoveryDisplays.Length == 1 &&
            localizedIndirectRecoveryDisplays[0].FriendlyName == "Panel interno",
            "Emergency recovery treated an indirect display with an unknown name as physical.");
        Require(StreamingHostLocator.ExtractExecutablePath(
                @"""C:\Program Files\Sunshine\sunshinesvc.exe"" --service") ==
                @"C:\Program Files\Sunshine\sunshinesvc.exe",
            "Quoted Sunshine service path parsing failed.");
        Require(StreamingHostLocator.ExtractExecutablePath(
                @"C:\Sunshine Portable\sunshine.exe --service") ==
                @"C:\Sunshine Portable\sunshine.exe",
            "Unquoted Sunshine service path parsing failed.");
        Require(HostRecoveryActions.IsProtectedProcessName("explorer"), "Windows shell protection failed.");
        Require(HostRecoveryActions.IsProtectedProcessName("StartMenuExperienceHost"), "Windows Start menu protection failed.");
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
        Console.WriteLine("VitaMoonlight.Host configure [--host sunshine|apollo] [--config-dir PATH] [--display-match TEXT] [--all-apps true|false] [--force-sdr true|false]");
        Console.WriteLine("VitaMoonlight.Host host status|restart [--host sunshine]");
        Console.WriteLine("VitaMoonlight.Host host ensure-compatible --installer PATH");
        Console.WriteLine("VitaMoonlight.Host gamepad status|ensure-compatible [--installer PATH]");
        Console.WriteLine("VitaMoonlight.Host runtime status|ensure-compatible [--installer PATH]");
        Console.WriteLine("VitaMoonlight.Host dependency uninstall sunshine|vigembus");
        Console.WriteLine("VitaMoonlight.Host state secure");
        Console.WriteLine("VitaMoonlight.Host driver install|reload|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host display list");
        Console.WriteLine("VitaMoonlight.Host display disable-virtual");
        Console.WriteLine("VitaMoonlight.Host session test --width N --height N --fps N [--seconds 5..120]");
        Console.WriteLine("VitaMoonlight.Host session start --width N --height N --fps N");
        Console.WriteLine("VitaMoonlight.Host session mode --width 960|1280 --height 540|544|720 [--fps 60]");
        Console.WriteLine("VitaMoonlight.Host session stop|recover|recover-upgrade|status");
        Console.WriteLine("VitaMoonlight.Host recovery install|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host agent run [--background]|install|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host emergency close-foreground|recover-display|reset-display-driver");
        Console.WriteLine("VitaMoonlight.Host uninstall prepare|cleanup-integration");
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

    private static string FormatErrorDetails(Exception error)
    {
        var messages = new List<string>();

        void AddError(Exception current)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 &&
                !messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    AddError(inner);
                }
            }
            else if (current.InnerException is not null)
            {
                AddError(current.InnerException);
            }
        }

        AddError(error);
        return string.Join(Environment.NewLine, messages);
    }

    private static void TryClearLastCommandError()
    {
        try
        {
            if (!InstallationTrust.IsInstalledPayload(out _)) return;
            if (!IsCurrentProcessAdministrator()) return;
            if (!MachineStateSecurity.IsProtectionInitialized()) return;
            MachineStateSecurity.Secure();
            TrustedFileSystem.DeleteFile(HostStatePaths.LastErrorFile);
        }
        catch
        {
            // A stale diagnostic breadcrumb must never block the requested command.
        }
    }

    private static void TryWriteLastCommandError(string details)
    {
        try
        {
            if (!InstallationTrust.IsInstalledPayload(out _)) return;
            if (!IsCurrentProcessAdministrator()) return;
            if (!MachineStateSecurity.IsProtectionInitialized()) return;
            MachineStateSecurity.Secure();
            TrustedFileSystem.WriteAllText(
                HostStatePaths.LastErrorFile,
                $"Vita Moonlight Host command failed at {DateTimeOffset.Now:O}{Environment.NewLine}{details}{Environment.NewLine}");
        }
        catch
        {
            // The original command failure remains authoritative.
        }
    }

    private static bool IsCurrentProcessAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static void EnsureWindows()
    {
        var platform = WindowsPlatformCompatibility.Inspect();
        if (!platform.IsSupported)
        {
            throw new PlatformNotSupportedException(platform.RequirementMessage);
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
        InstallationTrust.RequireInstalledPayload(operation);
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
    string Architecture,
    bool IsSupportedPlatform,
    bool IsAdministrator,
    string HostMode,
    string? SunshinePath,
    string? SunshineVersion,
    bool SunshineVersionSupported,
    string? ApolloPath,
    bool ViGEmBusInstalled,
    bool ViGEmBusRunning,
    bool SunshineNeedsRestart,
    string? DisplayWizardPath,
    string? VisualCppRuntimeVersion,
    bool VisualCppRuntimeSupported,
    bool VirtualDisplayDriverInstalled,
    bool VirtualDisplayModesReady,
    bool RecoveryPending,
    bool RecoveryTaskInstalled,
    bool RescueAgentInstalled,
    bool RescueAgentRunning,
    IReadOnlyList<HostModeHotkeyStatus> ModeHotkeys,
    bool IntegrateAllSunshineApps,
    bool ForceSdr,
    bool NativeDisplayLifecycleReady,
    string Recommendation)
{
    public bool HasStreamingHost => SunshinePath is not null || ApolloPath is not null;
    public bool HasSelectedStreamingHost => HostMode == "apollo" ? ApolloPath is not null : SunshinePath is not null;
    public bool HasDisplaySupport => HostMode == "apollo" ||
        (DisplayWizardPath is not null && VirtualDisplayDriverInstalled && VirtualDisplayModesReady);
    public bool ModeHotkeysReady => ModeHotkeys.Count == VitaDisplayModes.Supported.Count &&
        ModeHotkeys.All(status => status.Ready);
}

internal static class HostDiagnostics
{
    public static HostDiagnosticReport Inspect()
    {
        var settings = HostSettings.Load();
        var platform = WindowsPlatformCompatibility.Inspect();
        var isWindows = platform.IsWindows;
        var architecture = platform.IsWindows
            ? $"{platform.ArchitectureDescription}; {platform.InstallationType ?? "unknown Windows type"}"
            : platform.ArchitectureDescription;
        var supportedPlatform = platform.IsSupported;
        var sunshine = StreamingHostLocator.FindSunshineExecutable();
        var sunshineCompatibility = SunshineCompatibility.Inspect(sunshine);
        var apollo = StreamingHostLocator.FindApolloExecutable();
        string? displayWizard = null;
        try
        {
            var wizard = DisplayWizardAdapter.LocateBundled();
            wizard.ValidateDriverBundle();
            displayWizard = wizard.ExecutablePath;
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidDataException) { }
        var visualCppRuntime = VisualCppRuntimeCompatibility.Inspect();
        var vigemState = WindowsServiceManager.GetState("ViGEmBus");
        var vigem = vigemState != WindowsServiceState.NotInstalled;
        var vigemRunning = vigemState == WindowsServiceState.Running;
        var sunshineNeedsRestart = settings.HostMode == "sunshine" && vigemRunning && SunshineLogReportsMissingViGEm(settings);
        var virtualDisplay = DisplayWizardAdapter.IsDriverInstalled();
        var virtualDisplayModesReady =
            settings.HostMode == "apollo" ||
            (DisplayWizardAdapter.HasVitaCompatibilityModes() &&
             DriverNativeModeVerification.IsCurrent(out _));
        var recoveryPending = File.Exists(HostStatePaths.RecoveryFile);
        var recoveryTaskInstalled = RecoveryTaskManager.IsInstalled();
        var rescueAgentInstalled = HostRecoveryAgentManager.IsInstalled();
        var rescueAgentRunning = HostRecoveryAgentManager.IsRunning();
        var modeHotkeys = HostRecoveryAgentManager.GetModeHotkeyReadiness();
        var modeHotkeysReady = modeHotkeys.Count == VitaDisplayModes.Supported.Count &&
            modeHotkeys.All(status => status.Ready);
        var nativeDisplayLifecycleReady = settings.HostMode != "sunshine" || !settings.IntegrateAllSunshineApps ||
            SunshineConfigurator.IsNativeDisplayManagementReady(
                SunshineConfigurator.ResolveConfigurationDirectory(settings.SunshineConfigDirectory, "sunshine"));

        var recommendation = !isWindows
            ? "Run this companion on a Windows 10 version 2004 or newer / Windows 11 x64 streaming host."
            : !supportedPlatform
                ? platform.RequirementMessage
            : recoveryPending
                ? "An earlier session did not restore its display topology. Run `session recover` before streaming."
                : settings.HostMode == "apollo" && apollo is null
                    ? "Install Apollo or configure Sunshine mode, then run this check again."
                    : settings.HostMode == "sunshine" && sunshine is null
                        ? "Install Sunshine or configure Apollo mode, then run this check again."
                        : settings.HostMode == "sunshine" && !sunshineCompatibility.IsSupported
                            ? $"Update Sunshine to {SunshineCompatibility.MinimumVersionText} or newer with the Vita Moonlight host installer."
                    : settings.HostMode == "sunshine" && !visualCppRuntime.IsSupported
                        ? $"Install Microsoft Visual C++ runtime {VisualCppRuntimeCompatibility.MinimumVersionText} or newer by clicking Apply recommended setup."
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
                                : settings.HostMode == "sunshine" && !virtualDisplayModesReady
                                    ? "The virtual display modes are missing or native 960x544 has not been verified. Click Install/update display driver, then Apply recommended setup."
                                : !recoveryTaskInstalled
                                    ? "The host is ready, but automatic logon recovery is not installed. In the Administrator control panel, click Install recovery safeguard."
                                : !rescueAgentInstalled || !rescueAgentRunning
                                    ? "Install or repair the stream rescue agent from Help & recovery so the Vita can close a hung game or recover the host display."
                                : !modeHotkeysReady
                                    ? "One or more display-mode rescue hotkeys are unavailable. Close software using Ctrl+Alt+Shift+F8/F9/F10, then repair the stream rescue agent."
                                : "The host is ready. Click Apply recommended setup after an install or update, then launch any Sunshine application from the Vita.";

        return new HostDiagnosticReport(
            isWindows,
            Environment.OSVersion.VersionString,
            architecture,
            supportedPlatform,
            IsAdministrator(),
            settings.HostMode,
            sunshine,
            sunshineCompatibility.DetectedVersion,
            sunshineCompatibility.IsSupported,
            apollo,
            vigem,
            vigemRunning,
            sunshineNeedsRestart,
            displayWizard,
            visualCppRuntime.DetectedVersion,
            visualCppRuntime.IsSupported,
            virtualDisplay,
            virtualDisplayModesReady,
            recoveryPending,
            recoveryTaskInstalled,
            rescueAgentInstalled,
            rescueAgentRunning,
            modeHotkeys,
            settings.IntegrateAllSunshineApps,
            settings.ForceSdr,
            nativeDisplayLifecycleReady,
            recommendation);
    }

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
