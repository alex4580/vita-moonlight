using System.ComponentModel;
using System.Runtime.ExceptionServices;
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
    private const int ExitBackendDisabled = 5;
    private const int ExitBackendStateError = 6;

    [STAThread]
    public static int Main(string[] args)
    {
        var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "gui";
        var remaining = args.Skip(1).ToArray();
        if (command != "self-test")
        {
            ClearOperationalPathOverrides();
        }
        if (string.Equals(
                command,
                AudioEndpointRecoveryService.IsolatedWorkerCommand,
                StringComparison.Ordinal))
        {
            // This exact internal action runs in a disposable child process
            // so stalled Core Audio COM/RPC can be terminated without hanging
            // stream stop, recovery, Pause, or uninstall. It accepts no
            // endpoint ID from the command line and touches only protected
            // journal state, so it remains safe during a maintenance fence.
            try
            {
                return AudioEndpointRecoveryService.RunIsolatedWorker(
                    remaining);
            }
            catch (Exception workerError)
            {
                Console.Error.WriteLine(workerError.Message);
                return ExitFailure;
            }
        }
        if (TryDescribeUnrelatedSunshineHook(
                command,
                remaining,
                out var ignoredHookMessage))
        {
            // This path is deliberately before the installer-maintenance gate
            // and all machine-state access. It is a proven no-op for a
            // non-Vita Sunshine client and must never block that stream.
            Console.WriteLine(ignoredHookMessage);
            return ExitSuccess;
        }
        TryClearLastCommandError();
        try
        {
            using var maintenanceAccess =
                InstallerMaintenanceFence.AuthorizeCommand(
                    command,
                    remaining);
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
                "maintenance" => MaintenanceCommand(remaining),
                "deferred-setup" => DeferredSetupCommand(remaining),
                "backend" => BackendCommand(remaining),
                "driver" => DriverCommand(remaining),
                "display" => DisplayCommand(remaining),
                "session" => SessionCommand(remaining),
                "recovery" => RecoveryCommand(remaining),
                "agent" => AgentCommand(remaining),
                "emergency" => EmergencyCommand(remaining),
                "support" => SupportCommand(remaining),
                "uninstall" => UninstallCommand(remaining),
                "self-test" => RunSelfTest(),
                "help" or "--help" or "-h" => PrintHelp(),
                _ => InvalidCommand(command),
            };
        }
        catch (HostRestartRequiredException error)
        {
            Console.Error.WriteLine($"Restart required: {error.Message}");
            TryWriteLastCommandError("Restart required: " + error.Message);
            TryWriteMaintenanceHelperError(
                "Restart required: " + error.Message);
            return ExitRestartRequired;
        }
        catch (Exception error)
        {
            var errorDetails = FormatErrorDetails(error);
            Console.Error.WriteLine($"Error: {errorDetails}");
            TryWriteLastCommandError(errorDetails);
            TryWriteMaintenanceHelperError(errorDetails);
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

    private static int Configure(
        string[] args,
        bool allowDeferredSetupTransition = false)
    {
        EnsureWindows();
        EnsureAdministrator("Configuring the streaming host");
        using var operation = BackendLifecycleStateStore.AcquireLock();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        return ConfigureLocked(
            args,
            operation,
            allowDeferredSetupTransition);
    }

    private static int ConfigureLocked(
        string[] args,
        BackendOperationLease operation,
        bool allowDeferredSetupTransition = false)
    {
        operation.RequireActive();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        if (!allowDeferredSetupTransition)
        {
            EnsureBackendEnabled(
                "Host configuration is paused. Run `backend enable` first");
        }
        var previous = HostSettings.Load();
        var hostMode = GetOption(args, "--host") ?? previous.HostMode;
        if (!hostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase) &&
            !hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--host must be `sunshine` or `apollo`.");
        }

        var hostModeChanged = !hostMode.Equals(
            previous.HostMode,
            StringComparison.OrdinalIgnoreCase);
        var configuredDirectory = GetOption(args, "--config-dir");
        var configuredDisplayMatch = GetOption(args, "--display-match");

        var settings = previous with
        {
            HostMode = hostMode.ToLowerInvariant(),
            SunshineConfigDirectory = configuredDirectory ??
                (hostModeChanged ? null : previous.SunshineConfigDirectory),
            SunshineApplicationName = GetOption(args, "--app") ?? previous.SunshineApplicationName,
            DisplayWizardPath = previous.DisplayWizardPath,
            DisplayMatch = configuredDisplayMatch ??
                (hostModeChanged ? null : previous.DisplayMatch),
            IntegrateAllSunshineApps = GetOptionalBool(args, "--all-apps") ?? previous.IntegrateAllSunshineApps,
            ForceSdr = GetOptionalBool(args, "--force-sdr") ?? previous.ForceSdr,
        };

        if (settings.HostMode == "apollo")
        {
            if (StreamingHostLocator.FindApolloExecutable() is null)
            {
                throw new InvalidOperationException(
                    "Apollo was not found. Apollo is an experimental CLI-only path in this beta and is never installed by Vita Moonlight Host.");
            }
            if (string.IsNullOrWhiteSpace(settings.DisplayMatch))
            {
                throw new InvalidOperationException(
                    "Experimental Apollo configuration requires an explicit --display-match value for Apollo's virtual display. Automatic selection is intentionally disabled so another virtual display cannot be hijacked.");
            }
            if (StreamingHostLocator.IsApolloRunning())
            {
                throw new InvalidOperationException(
                    "Exit Apollo from its tray icon before changing its configuration. Apollo support is experimental in this beta.");
            }
        }

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
                    "Open Get started and choose Set up or repair this PC first.");
            }

            using (var transaction = DisplayTransactionLock.Acquire())
            {
                BackendLifecycleStateStore.RequireNoUninstallInProgress();
                var modesChanged =
                    wizard.EnsureVitaCompatibilityModes(transaction);
                var verificationCurrent =
                    DriverNativeModeVerification.IsCurrent(out _);
                if (RequiresNativeModeVerification(
                        modesChanged,
                        verificationCurrent))
                {
                    verifyNativeMode = true;
                }
            }
            settings = settings with { DisplayWizardPath = wizard.ExecutablePath };
        }

        var removeLegacyApolloIntegration = false;
        if (settings.HostMode == "sunshine")
        {
            var ownershipState = SunshineOwnershipJournal.Load();
            if (previous.HostMode.Equals(
                    "apollo",
                    StringComparison.OrdinalIgnoreCase))
            {
                var previousConfigurationDirectory =
                    SunshineConfigurator.ResolveConfigurationDirectory(
                        previous.SunshineConfigDirectory,
                        "apollo");
                if (SunshineOwnershipJournal.TagExistingLocation(
                        ownershipState,
                        previousConfigurationDirectory,
                        "apollo"))
                {
                    SunshineOwnershipJournal.Save(ownershipState);
                }
            }
            removeLegacyApolloIntegration =
                SunshineOwnershipJournal.HasLocationForHost(
                    ownershipState,
                    "apollo");
            if (removeLegacyApolloIntegration &&
                StreamingHostLocator.IsApolloRunning())
            {
                throw new InvalidOperationException(
                    "Exit Apollo from its tray icon before migrating this PC to the supported Sunshine configuration. Apollo itself and unrelated Apollo settings will be preserved.");
            }
        }

        var companionPath = GetOption(args, "--companion") ?? Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe");
        if (verifyNativeMode)
        {
            var verificationResult = PrimeDriverOrReport("prepared");
            if (verificationResult != ExitSuccess)
            {
                return verificationResult;
            }
        }
        DateTimeOffset? inventoryNotBeforeUtc = null;
        if (settings.HostMode == "sunshine" &&
            settings.IntegrateAllSunshineApps)
        {
            if (!DriverNativeModeVerification.TryGetCurrent(
                    out var verification,
                    out var verificationMessage) ||
                verification is null)
            {
                throw new InvalidOperationException(
                    "The Vita virtual display cannot be bound safely because its native-mode verification is not current: " +
                    verificationMessage);
            }
            inventoryNotBeforeUtc = verification.VerifiedAt;
        }
        var result = SunshineConfigurator.Configure(
            settings,
            Path.GetFullPath(companionPath),
            inventoryNotBeforeUtc);
        if (removeLegacyApolloIntegration)
        {
            SunshineConfigurator.RemoveManagedIntegrationForHost("apollo");
        }
        // Commit the selected host/settings only after the corresponding host
        // files and any legacy-host cleanup have succeeded. A failed or killed
        // migration therefore remains retryable from the previous selection.
        settings.Save();
        Console.WriteLine($"Configured {settings.HostMode} in {result.ConfigurationDirectory}");
        Console.WriteLine($"Moonlight application: {result.ApplicationName}");
        Console.WriteLine(result.UsesAuthenticatedStreamBoundary
            ? "Virtual-display integration: authenticated Vita handoff for Sunshine launch and resume"
            : $"Virtual-display integration: prep hook ({result.CoveredApplicationCount} application(s))");
        Console.WriteLine($"Force SDR: {(settings.ForceSdr ? "enabled" : "disabled")}");
        Console.WriteLine($"Original apps backup: {result.BackupPath}");
        Console.WriteLine(settings.HostMode == "apollo"
            ? "Experimental Apollo configuration written. Restart Apollo from its tray icon, then select the configured application on the Vita."
            : "Restart Sunshine, then select the configured application on the Vita.");
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
            var restored = new SessionManager().RecoverToIdle();
            Console.WriteLine(restored
                ? "The Vita session was restored to the physical desktop and its virtual display device was disabled."
                : "The physical desktop is active and the idle Vita virtual display device is disabled.");
            return ExitSuccess;
        }
        if (action != "list")
        {
            return InvalidCommand($"display {action}");
        }

        foreach (var display in displays.ListDisplays())
        {
            Console.WriteLine($"[{display.PathIndex}] {(display.IsActive ? "active" : "inactive"),-8} {(display.IsAvailable ? "available" : "unavailable"),-11} {display.FriendlyName}");
            if (display.Width is not null &&
                display.Height is not null &&
                display.RefreshRate is not null)
            {
                Console.WriteLine(
                    $"    mode {display.Width}x{display.Height}@{display.RefreshRate}");
            }
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
            EnsureBackendEnabled(
                "Vita host features are paused. Enable them before changing Vita-managed Sunshine integration");
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
                EnsureBackendEnabled(
                    "Vita host features are paused. Enable them before a Vita-requested Sunshine restart");
                var inventoryNotBefore = DateTimeOffset.UtcNow;
                WindowsServiceManager.Restart(StreamingHostLocator.FindSunshineServiceName(), "Sunshine");
                var settings = HostSettings.Load();
                if (settings.IntegrateAllSunshineApps)
                {
                    // Supported authenticated handoff keeps the exact VDD
                    // disabled while idle, so a Sunshine INFO inventory is
                    // neither available nor authoritative. Verify the exact
                    // journal-owned PnP device instead; stream preflight will
                    // enable it before Sunshine probes the active output.
                    _ = ManagedVddOwnershipJournal
                        .RequireOwnedPresentDevices(required: true);
                }
                else
                {
                    var configDirectory = SunshineConfigurator
                        .ResolveConfigurationDirectory(
                            settings.SunshineConfigDirectory,
                            "sunshine");
                    var displayDeviceId = SunshineConfigurator
                        .WaitForManagedDisplayDeviceId(
                            Path.Combine(configDirectory, "sunshine.log"),
                            displayMatch: null,
                            timeoutMilliseconds: 30000,
                            pollMilliseconds: 250,
                            inventoryNotBeforeUtc: inventoryNotBefore);
                    if (displayDeviceId is null)
                    {
                        throw new InvalidOperationException(
                            "Sunshine restarted, but its new process did not enumerate the legacy managed virtual display within 30 seconds. " +
                            "Keep a physical display enabled, repair the Vita display driver, and try setup again.");
                    }
                }
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
        if (action is "install" or "reload")
        {
            EnsureBackendEnabled(
                "Virtual display maintenance is paused. Run `backend enable` first");
        }
        switch (action)
        {
            case "install":
                {
                    var expectedExistingDeviceInstanceId =
                        GetExpectedVddAdoptionInstanceId(args);
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
                    return PrimeDriverOrReport(
                        "installed",
                        installDriver: true,
                        expectedExistingDeviceInstanceId:
                            expectedExistingDeviceInstanceId);
                }
            case "reload":
                {
                    return PrimeDriverOrReport("reloaded");
                }
            case "adopt-idle":
                {
                    var ownerProcessId = GetRequiredInt(
                        args,
                        "--adoption-owner-pid");
                    var expectedExistingDeviceInstanceId =
                        InstallerMaintenanceFence
                            .RequireStagedVddAdoptionCandidateForOwner(
                                ownerProcessId);
                    var suspendBeforeLease =
                        DisplaySuspendIntentStore.Inspect();
                    using var transaction =
                        DisplayTransactionLock.Acquire();
                    DisplaySuspendIntentStore
                        .RequireClearForDisplayMutationLocked(
                            transaction,
                            suspendBeforeLease,
                            "Approved virtual-display adoption");
                    BackendLifecycleStateStore
                        .RequireNoUninstallInProgress();
                    var adoption = ManagedVddOwnershipJournal
                        .PrepareInstallLocked(
                            transaction,
                            expectedExistingDeviceInstanceId);
                    if (adoption.Action !=
                            ManagedVddInstallAction.UseOwnedInstance ||
                        adoption.Device?.Ownership !=
                            ManagedVddOwnershipKind.Adopted)
                    {
                        throw new InvalidOperationException(
                            "The approved existing virtual display was not adopted. No different device was created or changed.");
                    }
                    var idle = ManagedVirtualDisplayRuntime
                        .ReconcileIdleLocked(
                            transaction,
                            requireManagedDevice: true);
                    DisplaySuspendIntentStore
                        .RequireClearAfterDisplayMutationLocked(
                            transaction,
                            "Approved virtual-display adoption");
                    Console.WriteLine(
                        $"Adopted exact virtual-display instance {adoption.Device.InstanceId}, recorded its enabled baseline, and safely disabled it while Vita host features remain paused. Physical display(s): {string.Join(", ", idle.PhysicalDisplays)}.");
                    return ExitSuccess;
                }
            case "uninstall":
                {
                    var wizard =
                        DisplayWizardAdapter.LocateBundledForUninstall();
                    bool restartRequired;
                    using (var transaction = DisplayTransactionLock.Acquire())
                    {
                        restartRequired = wizard.UninstallDriver(transaction);
                    }
                    Console.WriteLine(restartRequired
                        ? "The Vita virtual display driver was removed. Windows requires a restart to finish cleanup."
                        : "The Vita virtual display driver and its managed configuration were removed.");
                    return restartRequired ? ExitRestartRequired : ExitSuccess;
                }
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

    private static int MaintenanceCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "begin":
                EnsureMaintenanceAdministrator(
                    "Beginning installer maintenance");
                var ownerProcessId = GetRequiredInt(
                    args,
                    "--owner-pid");
                var state = InstallerMaintenanceFence.Begin(ownerProcessId);
                Console.WriteLine(
                    $"Installer maintenance is owned by live process {state.OwnerProcessId}.");
                return ExitSuccess;
            case "end":
                EnsureMaintenanceAdministrator(
                    "Ending installer maintenance");
                var endingOwnerProcessId = GetRequiredInt(
                    args,
                    "--owner-pid");
                Console.WriteLine(
                    InstallerMaintenanceFence.End(endingOwnerProcessId)
                        ? "Installer maintenance ended."
                        : "No installer maintenance fence remained.");
                return ExitSuccess;
            case "status":
                var status = InstallerMaintenanceFence.Inspect();
                if (status is null)
                {
                    Console.WriteLine("No installer maintenance is active.");
                    return ExitMissingRequiredComponent;
                }
                Console.WriteLine(
                    $"Installer maintenance owner: process {status.State.OwnerProcessId} " +
                    $"({(status.OwnerIsAlive ? "running" : "not running")}), " +
                    $"began {status.State.BeganAtUtc.LocalDateTime:g}.");
                return status.OwnerIsAlive
                    ? ExitSuccess
                    : ExitFailure;
            case "backend-was-enabled":
            case "rescue-task-was-present":
            case "recovery-task-was-present":
                EnsureMaintenanceAdministrator(
                    "Reading the protected installer-maintenance snapshot");
                var snapshotOwnerProcessId = GetRequiredInt(
                    args,
                    "--owner-pid");
                var snapshot = InstallerMaintenanceFence.GetSnapshotForOwner(
                    snapshotOwnerProcessId);
                var required = action switch
                {
                    "backend-was-enabled" => snapshot.BackendWasEnabled,
                    "rescue-task-was-present" =>
                        snapshot.RescueAgentTaskWasPresent,
                    _ => snapshot.RecoveryTaskWasPresent,
                };
                Console.WriteLine(required ? "present" : "not present");
                return required
                    ? ExitSuccess
                    : ExitMissingRequiredComponent;
            case "vdd-adoption-required":
                EnsureMaintenanceAdministrator(
                    "Checking whether the existing virtual display needs explicit adoption");
                var adoptionOwnerProcessId = GetRequiredInt(
                    args,
                    "--owner-pid");
                _ = InstallerMaintenanceFence.GetSnapshotForOwner(
                    adoptionOwnerProcessId);
                var adoptionRequired = ManagedVddOwnershipJournal
                    .RequiresExplicitAdoption(
                        out var adoptionReason,
                        out var adoptionCandidateInstanceId);
                InstallerMaintenanceFence
                    .StageVddAdoptionCandidateForOwner(
                        adoptionOwnerProcessId,
                        adoptionRequired
                            ? adoptionCandidateInstanceId
                            : null);
                Console.WriteLine(adoptionReason);
                return adoptionRequired
                    ? ExitSuccess
                    : ExitMissingRequiredComponent;
            default:
                return InvalidCommand($"maintenance {action}");
        }
    }

    private static int BackendCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        BackendLifecycleReport report;
        switch (action)
        {
            case "enable":
                EnsureAdministrator("Enabling Vita Moonlight background functions");
                using (var operation = BackendLifecycleStateStore.AcquireLock())
                {
                    var pendingSetup = DeferredHostSetupStore.Load();
                    if (pendingSetup?.HostMode == "apollo")
                    {
                        throw new InvalidOperationException(
                            "This paused installation contains an older experimental Apollo setup plan. Run the current installer with its recommended Sunshine option to replace that plan before enabling Vita host features. Apollo itself and unrelated settings will be preserved.");
                    }
                    report = BackendLifecycleManager.EnableLocked(operation);
                    PrintBackendReport(report, HasFlag(args, "--json"));
                    if (report.Status != BackendLifecycleStatus.Enabled)
                    {
                        return ExitFailure;
                    }
                    return CompleteDeferredHostSetupAfterEnable(operation);
                }
            case "disable":
                EnsureAdministrator("Disabling Vita Moonlight background functions");
                report = BackendLifecycleManager.Disable();
                PrintBackendReport(report, HasFlag(args, "--json"));
                return report.Status == BackendLifecycleStatus.Disabled
                    ? ExitSuccess
                    : ExitFailure;
            case "status":
                report = BackendLifecycleManager.Inspect();
                PrintBackendReport(report, HasFlag(args, "--json"));
                if (HasFlag(args, "--intent-exit-code"))
                {
                    return BackendLifecycleManager.ReadPreference().State switch
                    {
                        BackendPreferenceState.Enabled => ExitSuccess,
                        BackendPreferenceState.Disabled => ExitBackendDisabled,
                        _ => ExitBackendStateError,
                    };
                }
                if (HasFlag(args, "--require-enabled") &&
                    report.Status != BackendLifecycleStatus.Enabled)
                {
                    return ExitMissingRequiredComponent;
                }
                return report.Status is BackendLifecycleStatus.Error or
                    BackendLifecycleStatus.Partial
                    ? ExitFailure
                    : ExitSuccess;
            default:
                return InvalidCommand($"backend {action}");
        }
    }

    private static int DeferredSetupCommand(string[] args)
    {
        EnsureWindows();
        EnsureAdministrator("Saving deferred Vita host setup");
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        switch (action)
        {
            case "save":
                var hostMode = GetOption(args, "--host")
                    ?? throw new ArgumentException(
                        "deferred-setup save requires --host sunshine.");
                if (!hostMode.Equals(
                        "sunshine",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "Deferred public-beta setup supports Sunshine only. Apollo remains an experimental explicit CLI configuration path.");
                }
                var installVirtualDisplay =
                    GetOptionalBool(args, "--virtual-driver") ?? false;
                var existingDeviceAdoptionInstanceId =
                    GetExpectedVddAdoptionInstanceId(args);
                DeferredHostSetupStore.Save(
                    hostMode,
                    installVirtualDisplay,
                    existingDeviceAdoptionInstanceId);
                Console.WriteLine(
                    "Saved the protected setup work which will run the next time Vita host features are enabled.");
                return ExitSuccess;
            case "clear":
                DeferredHostSetupStore.Delete();
                Console.WriteLine("No deferred Vita host setup remains.");
                return ExitSuccess;
            case "status":
                var plan = DeferredHostSetupStore.Load();
                Console.WriteLine(plan is null
                    ? "No deferred Vita host setup remains."
                    : $"Deferred Vita host setup: {plan.HostMode}, virtual display {(plan.InstallVirtualDisplay ? "install/repair" : "keep existing")}, existing-device adoption {(plan.ExistingDeviceAdoptionInstanceId is null ? "not required" : "bound to " + plan.ExistingDeviceAdoptionInstanceId)}.");
                return plan is null
                    ? ExitMissingRequiredComponent
                    : ExitSuccess;
            default:
                return InvalidCommand($"deferred-setup {action}");
        }
    }

    private static int CompleteDeferredHostSetupAfterEnable(
        BackendOperationLease operation)
    {
        operation.RequireActive();
        BackendLifecycleStateStore.RequireNoUninstallInProgress();
        var plan = DeferredHostSetupStore.Load();
        if (plan is null) return ExitSuccess;

        try
        {
            // The caller keeps the backend operation lease from the first
            // Enable transition through plan deletion. A second Enable and
            // uninstall therefore cannot install, configure, or remove the
            // same integration concurrently.
            if (plan.HostMode == "sunshine")
            {
                var sunshineInstaller = Path.Combine(
                    AppContext.BaseDirectory,
                    "tools",
                    "Sunshine",
                    "Sunshine-Windows-AMD64-installer.msi");
                var sunshineResult =
                    SunshineCompatibility.EnsureCompatible(
                        sunshineInstaller);
                if (sunshineResult.RestartRequired)
                {
                    throw new HostRestartRequiredException(
                        "Sunshine was updated. Restart Windows, then enable Vita host features again to finish the deferred setup.");
                }
            }

            if (plan.InstallVirtualDisplay)
            {
                var runtimeInstaller = Path.Combine(
                    AppContext.BaseDirectory,
                    "tools",
                    "DisplayWizard",
                    "VC_redist.x64.exe");
                var runtimeResult =
                    VisualCppRuntimeCompatibility.EnsureCompatible(
                        runtimeInstaller);
                if (runtimeResult.RestartRequired)
                {
                    throw new HostRestartRequiredException(
                        "The virtual-display runtime was updated. Restart Windows, then enable Vita host features again to finish the deferred setup.");
                }

                var driverResult = PrimeDriverOrReport(
                    "installed",
                    installDriver: true,
                    expectedExistingDeviceInstanceId:
                        plan.ExistingDeviceAdoptionInstanceId);
                if (driverResult != ExitSuccess)
                {
                    throw new InvalidOperationException(
                        "Deferred virtual-display verification did not complete successfully.");
                }
            }

            if (plan.HostMode == "sunshine")
            {
                WindowsServiceManager.Restart(
                    StreamingHostLocator.FindSunshineServiceName(),
                    "Sunshine");
            }
            var configureResult = ConfigureLocked(
                ["--host", plan.HostMode],
                operation,
                allowDeferredSetupTransition: true);
            if (configureResult != ExitSuccess)
            {
                throw new InvalidOperationException(
                    "Deferred streaming-host configuration did not complete successfully.");
            }
            if (plan.HostMode == "sunshine")
            {
                WindowsServiceManager.Restart(
                    StreamingHostLocator.FindSunshineServiceName(),
                    "Sunshine");
            }

            DeferredHostSetupStore.DeleteLocked(operation);
            Console.WriteLine(
                "Completed the setup work deferred while Vita host features were paused.");
            return ExitSuccess;
        }
        catch (Exception setupFailure)
        {
            try
            {
                var rollback = BackendLifecycleManager.DisableLocked(
                    operation,
                    allowUninstallRollback: false);
                if (rollback.Status != BackendLifecycleStatus.Disabled)
                {
                    throw new InvalidOperationException(
                        "Windows could not completely return Vita host features to their paused state: " +
                        string.Join(" ", rollback.Issues));
                }
            }
            catch (Exception rollbackError)
            {
                throw new AggregateException(
                    "Deferred host setup failed, and Vita host features could not be completely paused again.",
                    setupFailure,
                    rollbackError);
            }

            ExceptionDispatchInfo.Capture(setupFailure).Throw();
            return ExitFailure;
        }
    }

    private static void PrintBackendReport(
        BackendLifecycleReport report,
        bool json)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            return;
        }

        Console.WriteLine($"Vita host features: {report.Status}");
        Console.WriteLine($"Requested state: {report.DesiredState}");
        Console.WriteLine(report.PreferencePersisted
            ? $"Preference saved for this PC: {report.UpdatedAtUtc?.LocalDateTime:g}"
            : "No saved preference; existing installations default to enabled.");
        if (report.Components is not null)
        {
            Console.WriteLine(
                $"Recovery task: {(report.Components.RecoveryTaskInstalled ? "installed" : "not installed")}");
            Console.WriteLine(
                $"Rescue agent: {(report.Components.RescueAgentTaskInstalled ? "installed" : "not installed")}, " +
                $"{(report.Components.RescueAgentRunning ? "running" : "stopped")}");
            Console.WriteLine(
                $"Vita display device: {(report.Components.ManagedVirtualDisplayInstalled ? "installed" : "not installed")}, " +
                $"{(report.Components.ManagedVirtualDisplayEnabled ? "enabled" : "disabled")}, " +
                $"{(report.Components.ManagedVirtualDisplayActive ? "active" : "inactive")}");
            Console.WriteLine(
                "Shared Sunshine: left unchanged by Vita host-feature pause/enable.");
        }
        foreach (var issue in report.Issues)
        {
            Console.WriteLine($"Attention: {issue}");
        }
    }

    private static int PrimeDriverOrReport(
        string action,
        bool installDriver = false,
        string? expectedExistingDeviceInstanceId = null)
    {
        try
        {
            var mode = new SessionManager()
                .PrimeNativeModeForDriverMaintenanceOnly(
                    installDriver,
                    expectedExistingDeviceInstanceId);
            Console.WriteLine(
                $"Virtual display driver {action} and verified at " +
                $"{mode.Width}x{mode.Height}@{mode.DesktopRefreshRate}. " +
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
                $"Virtual display driver: installed, but {verification}. Open Display & recovery and choose Repair Vita display driver.";
            return false;
        }
        try
        {
            // A hardware ID is not ownership. Readiness must resolve the
            // exact protected instance used by stream start/stop; otherwise a
            // third-party MTT node could appear healthy here and fail only
            // after the Vita has already attempted to connect.
            var managedDevice = ManagedVddOwnershipJournal
                .RequireOwnedPresentDevices(required: true)
                .Single();
            if (!managedDevice.Enabled)
            {
                message =
                    "Virtual display driver: ready and safely disabled while idle.";
                return true;
            }
            if (!File.Exists(HostStatePaths.RecoveryFile))
            {
                message =
                    "Virtual display driver: the managed device is enabled without an active journaled stream. Run Repair Vita display driver to return it to the safe idle state.";
                return false;
            }
            var display = new DisplayTopologyService().ListDisplays().FirstOrDefault(candidate =>
                candidate.IsAvailable && DisplayTopologyService.IsManagedVirtualDisplay(candidate));
            if (display is null)
            {
                message =
                    "Virtual display driver: installed, but Windows has not enumerated the display. Restart Windows.";
                return false;
            }
            message =
                $"Virtual display driver: active for a journaled stream ({display.FriendlyName}).";
            return true;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                Win32Exception)
        {
            message = $"Virtual display driver: not ready ({error.Message}).";
            return false;
        }
    }

    private static int SessionCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
        VitaStreamMode? hookStreamMode = null;
        if (action == "hook-start")
        {
            var width = GetRequiredInt(args, "--width");
            var height = GetRequiredInt(args, "--height");
            var streamFps = GetRequiredInt(args, "--fps");
            if (!VitaDisplayModes.TryGetSupportedStreamMode(
                    width,
                    height,
                    streamFps,
                    out var recognizedMode))
            {
                // Sunshine applications can be launched by non-Vita clients.
                // A prep hook must not block those sessions or change their
                // display merely because their mode is outside our contract.
                Console.WriteLine(
                    $"Sunshine client mode {width}x{height} at {streamFps} FPS " +
                    "is not a Vita mode; the host display was left unchanged.");
                return ExitSuccess;
            }
            hookStreamMode = recognizedMode;
        }
        if (action != "status")
        {
            EnsureAdministrator(
                "Managing the Vita streaming display session");
        }
        var manager = new SessionManager();
        if (action is "start" or "test" or "mode" || hookStreamMode is not null)
        {
            EnsureBackendReadyForStreaming();
        }
        switch (action)
        {
            case "hook-start":
                var hookResult = manager.Start(
                    hookStreamMode!.Value.DesktopMode.Width,
                    hookStreamMode.Value.DesktopMode.Height,
                    hookStreamMode.Value.StreamFps);
                Console.WriteLine(
                    $"Vita hook display active: {hookResult.DisplayName} at " +
                    $"{hookResult.Width}x{hookResult.Height}@{hookResult.DesktopRefreshRate} Hz " +
                    $"(stream {hookResult.StreamFps} FPS)");
                return ExitSuccess;
            case "start":
                var result = manager.Start(
                    GetRequiredInt(args, "--width"),
                    GetRequiredInt(args, "--height"),
                    GetRequiredInt(args, "--fps"));
                Console.WriteLine(
                    $"Streaming display active: {result.DisplayName} at " +
                    $"{result.Width}x{result.Height}@{result.DesktopRefreshRate} Hz " +
                    $"(stream {result.StreamFps} FPS)");
                return ExitSuccess;
            case "test":
                var seconds = GetOptionalInt(args, "--seconds", 15, 5, 120);
                SessionStartResult? testResult = null;
                var restoredOwnTest = false;
                try
                {
                    testResult = manager.Start(
                        GetRequiredInt(args, "--width"),
                        GetRequiredInt(args, "--height"),
                        GetRequiredInt(args, "--fps"));
                    Console.WriteLine(
                        $"Test display active: {testResult.DisplayName} at " +
                        $"{testResult.Width}x{testResult.Height}@{testResult.DesktopRefreshRate} Hz " +
                        $"(stream {testResult.StreamFps} FPS)");
                    Console.WriteLine($"Restoring the original display layout in {seconds} seconds...");
                    Thread.Sleep(TimeSpan.FromSeconds(seconds));
                }
                finally
                {
                    if (testResult is not null)
                    {
                        restoredOwnTest = manager.RestoreIfPending(
                            testResult.RecoveryCapturedAt);
                    }
                }
                Console.WriteLine(restoredOwnTest
                    ? "Display test completed and the original topology was restored."
                    : "Display test completed after its transaction had already ended; any newer session was left unchanged.");
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
                Console.WriteLine(manager.RestoreIfPending()
                    ? "Original display topology restored."
                    : "No pending display recovery was found.");
                return ExitSuccess;
            case "recover":
                Console.WriteLine(manager.RecoverToIdle()
                    ? "Original display topology restored and the idle Vita display was disabled."
                    : "Physical display verified and the idle Vita display was disabled.");
                return ExitSuccess;
            case "recover-upgrade":
                var safeRecovery =
                    UninstallManager
                        .RecoverPhysicalAndDiscardPendingTransactionForRecoveryUpgradeOnly();
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
                EnsureBackendEnabled(
                    "Automatic recovery is paused. Run `backend enable` first");
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
                var recoveryProbe = RecoveryTaskManager.GetInstallationState();
                Console.WriteLine(recoveryProbe.State switch
                {
                    ExactScheduledTaskState.Present =>
                        "The automatic logon recovery task is installed.",
                    ExactScheduledTaskState.Missing =>
                        "The automatic logon recovery task is not installed.",
                    _ =>
                        "Windows could not inspect the exact automatic recovery task: " +
                        (recoveryProbe.Error ?? "unknown Task Scheduler error"),
                });
                return recoveryProbe.State switch
                {
                    ExactScheduledTaskState.Present => ExitSuccess,
                    ExactScheduledTaskState.Missing => ExitMissingRequiredComponent,
                    _ => ExitFailure,
                };
            case "task-status":
                var exactRecoveryProbe =
                    RecoveryTaskManager.GetInstallationState();
                Console.WriteLine(exactRecoveryProbe.State switch
                {
                    ExactScheduledTaskState.Present => "present",
                    ExactScheduledTaskState.Missing => "missing",
                    _ => "unknown: " +
                        (exactRecoveryProbe.Error ?? "Task Scheduler error"),
                });
                return exactRecoveryProbe.State switch
                {
                    ExactScheduledTaskState.Present => ExitSuccess,
                    ExactScheduledTaskState.Missing =>
                        ExitMissingRequiredComponent,
                    _ => ExitFailure,
                };
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
                EnsureBackendEnabled(
                    "The stream rescue agent is paused. Run `backend enable` first");
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
                var agentProbe = HostRecoveryAgentManager.GetInstallationState();
                var installed = agentProbe.State == ExactScheduledTaskState.Present;
                var running = HostRecoveryAgentManager.IsRunning();
                var modeHotkeys = HostRecoveryAgentManager.GetModeHotkeyReadiness();
                Console.WriteLine($"Stream rescue task: {agentProbe.State switch
                {
                    ExactScheduledTaskState.Present => "installed",
                    ExactScheduledTaskState.Missing => "not installed",
                    _ => "inspection failed: " +
                        (agentProbe.Error ?? "unknown Task Scheduler error"),
                }}");
                Console.WriteLine($"Stream rescue agent: {(running ? "running" : "not running")}");
                if (modeHotkeys.Count == 0)
                {
                    Console.WriteLine(
                        "Display mode control: authenticated Vita launch/resume preflight (legacy F8-F10 shortcuts are not registered).");
                }
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
                return agentProbe.State == ExactScheduledTaskState.Unknown
                    ? ExitFailure
                    : installed && running && modeHotkeys.All(status => status.Ready)
                        ? ExitSuccess
                        : ExitMissingRequiredComponent;
            case "task-status":
                var exactAgentProbe =
                    HostRecoveryAgentManager.GetInstallationState();
                Console.WriteLine(exactAgentProbe.State switch
                {
                    ExactScheduledTaskState.Present => "present",
                    ExactScheduledTaskState.Missing => "missing",
                    _ => "unknown: " +
                        (exactAgentProbe.Error ?? "Task Scheduler error"),
                });
                return exactAgentProbe.State switch
                {
                    ExactScheduledTaskState.Present => ExitSuccess,
                    ExactScheduledTaskState.Missing =>
                        ExitMissingRequiredComponent,
                    _ => ExitFailure,
                };
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
        if (action == "finalize-owned")
        {
            var finalization = UninstallManager.FinalizeOwnedState(
                restoreSunshine: !HasFlag(args, "--sunshine-removed"),
                restoreManagedVdd: !HasFlag(args, "--vdd-removed"));
            Console.WriteLine(
                $"Confirmed physical-only display topology: {string.Join(", ", finalization.PhysicalDisplays)}.");
            Console.WriteLine(finalization.RescueAgentRemoved
                ? "Removed the Vita Moonlight stream-rescue task and agent."
                : "No Vita Moonlight stream-rescue task or agent remained.");
            Console.WriteLine(finalization.RecoveryTaskRemoved
                ? "Removed the Vita Moonlight automatic recovery task."
                : "No Vita Moonlight automatic recovery task remained.");
            Console.WriteLine(
                $"Removed {finalization.StateCleanup.RemovedFiles} exact Vita-owned state file(s) " +
                $"and {finalization.StateCleanup.RemovedDirectories} empty state directory/directories.");
            if (finalization.StateCleanup.RetainedEntries.Count > 0)
            {
                Console.WriteLine(
                    "Retained unrecognized or untrusted state entries for safety: " +
                    string.Join(", ", finalization.StateCleanup.RetainedEntries));
            }
            foreach (var warning in finalization.Warnings)
            {
                Console.Error.WriteLine("Uninstall warning: " + warning);
            }
            return ExitSuccess;
        }
        if (action != "prepare")
        {
            return InvalidCommand($"uninstall {action}");
        }

        var result = UninstallManager.Prepare(
            beginUninstallTransaction: HasFlag(args, "--begin"));
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
            Console.WriteLine($"Task account:  {Status(report.ScheduledTaskAccountReady, report.ScheduledTaskAccountMessage)}");
            var backendDescription = report.BackendStatus switch
            {
                BackendLifecycleStatus.Enabled => "enabled",
                BackendLifecycleStatus.Disabled => "paused by you; installed components and pairing are kept",
                BackendLifecycleStatus.Partial =>
                    $"{report.BackendDesiredState.ToString().ToLowerInvariant()} is only partly applied",
                _ => "protected lifecycle state could not be verified",
            };
            Console.WriteLine($"Vita features: {Status(
                report.BackendStatus is BackendLifecycleStatus.Enabled or BackendLifecycleStatus.Disabled,
                backendDescription)}");
            Console.WriteLine($"Host mode:     {report.HostMode}");
            Console.WriteLine($"App coverage:  {(report.IntegrateAllSunshineApps ? "every Sunshine app" : "Vita Moonlight app only")}");
            Console.WriteLine($"Display lifecycle: {Status(report.NativeDisplayLifecycleReady, report.NativeDisplayLifecycleReady ? "authenticated automatic handoff enabled" : "run Set up or repair this PC")}");
            Console.WriteLine($"Color mode:    {(report.ForceSdr ? "force SDR for Vita sessions" : "leave Windows color mode unchanged")}");
            Console.WriteLine($"Sunshine:      {Status(report.SunshinePath is not null, report.SunshinePath ?? "not found")}");
            Console.WriteLine($"Sunshine version: {Status(
                report.HostMode != "sunshine" || report.SunshineVersionSupported,
                report.SunshineVersion is null
                    ? $"not detected (requires {SunshineCompatibility.MinimumVersionText}+)"
                    : $"{report.SunshineVersion} (requires {SunshineCompatibility.MinimumVersionText}+)")}");
            Console.WriteLine($"Apollo:        {(report.ApolloPath is null ? "not found" : report.ApolloPath + " (detected; experimental and not public-beta qualified)")}");
            Console.WriteLine($"ViGEmBus:      {Status(report.ViGEmBusInstalled, report.ViGEmBusInstalled ? "installed" : "not detected")}");
            Console.WriteLine($"ViGEmBus state:{Status(report.ViGEmBusRunning, report.ViGEmBusRunning ? "running" : report.ViGEmBusInstalled ? "installed but not running" : "unavailable")}");
            Console.WriteLine($"Sunshine gamepad: {Status(!report.SunshineNeedsRestart, report.SunshineNeedsRestart ? "restart required - startup did not see ViGEmBus" : "ready")}");
            Console.WriteLine($"Driver bundle: {Status(report.DisplayWizardPath is not null, report.DisplayWizardPath ?? "not found")}");
            Console.WriteLine($"VDD runtime:   {Status(
                report.VisualCppRuntimeSupported,
                report.VisualCppRuntimeSupported
                    ? $"Microsoft Visual C++ {report.VisualCppRuntimeVersion}"
                    : $"missing or older than {VisualCppRuntimeCompatibility.MinimumVersionText}")}");
            Console.WriteLine($"Virtual display: {Status(report.VirtualDisplayDriverInstalled, report.VirtualDisplayDriverInstalled ? "signed managed driver installed" : "not detected")}");
            Console.WriteLine($"Vita display modes: {Status(report.VirtualDisplayModesReady, report.VirtualDisplayModesReady ? "provisioned; native 960x544 verified" : "missing or unverified - repair display driver")}");
            Console.WriteLine($"Recovery:      {Status(!report.RecoveryPending, report.RecoveryPending ? "pending - run session recover" : "none")}");
            var recoveryTaskDescription = report.BackendStatus == BackendLifecycleStatus.Disabled
                ? "paused and removed as requested"
                : report.RecoveryTaskState switch
                {
                    ExactScheduledTaskState.Present => "installed",
                    ExactScheduledTaskState.Missing => "not installed",
                    _ => "Windows could not inspect the exact task",
                };
            Console.WriteLine($"Recovery task: {Status(
                report.BackendStatus == BackendLifecycleStatus.Disabled ||
                report.RecoveryTaskState == ExactScheduledTaskState.Present,
                recoveryTaskDescription)}");
            var rescueDescription = report.RescueAgentInstalled && report.RescueAgentRunning
                ? "installed and running"
                : report.BackendStatus == BackendLifecycleStatus.Disabled
                    ? "paused and removed as requested"
                    : report.RescueAgentTaskState == ExactScheduledTaskState.Unknown
                        ? "Windows could not inspect the exact task"
                : !report.RescueAgentInstalled && report.RescueAgentRunning
                    ? "orphan agent running; scheduled task missing - repair required"
                    : report.RescueAgentInstalled
                        ? "installed but not running"
                        : "not installed";
            Console.WriteLine($"Stream rescue: {Status(
                report.BackendStatus == BackendLifecycleStatus.Disabled ||
                report.RescueAgentInstalled && report.RescueAgentRunning,
                rescueDescription)}");
            var modeHotkeyDescription = report.ModeHotkeys.Count == 0
                ? "authenticated Vita launch/resume preflight; legacy F8-F10 shortcuts are not registered"
                : string.Join(
                    ", ",
                    report.ModeHotkeys.Select(status =>
                        $"{status.Mode} {(status.Ready ? "ready" : "unavailable")}"));
            Console.WriteLine($"Display mode controls: {Status(
                report.BackendStatus == BackendLifecycleStatus.Disabled || report.ModeHotkeysReady,
                report.BackendStatus == BackendLifecycleStatus.Disabled
                    ? "paused; restored when Vita host features are enabled"
                    : modeHotkeyDescription)}");
            foreach (var issue in report.BackendIssues.Take(3))
            {
                Console.WriteLine($"Host-feature attention: {issue}");
            }
            Console.WriteLine();
            Console.WriteLine(report.Recommendation);
        }

        if (report.BackendStatus == BackendLifecycleStatus.Disabled)
        {
            return report.IsSupportedPlatform && !report.RecoveryPending
                ? ExitSuccess
                : ExitMissingRequiredComponent;
        }

        return report.BackendStatus == BackendLifecycleStatus.Enabled &&
            report.IsSupportedPlatform && report.HasSelectedStreamingHost &&
            report.ScheduledTaskAccountReady &&
            report.SunshineVersionSupported &&
            report.HasDisplaySupport && report.NativeDisplayLifecycleReady &&
            report.ViGEmBusInstalled && report.ViGEmBusRunning && !report.SunshineNeedsRestart && !report.RecoveryPending &&
            report.VisualCppRuntimeSupported &&
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

    private static int SupportCommand(string[] args)
    {
        EnsureWindows();
        var action = args.FirstOrDefault()?.ToLowerInvariant() ?? "export";
        if (action != "export")
        {
            throw new ArgumentException("Support action must be `export`.");
        }

        var output = GetOption(args, "--output");
        if (string.IsNullOrWhiteSpace(output))
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            output = Path.Combine(
                string.IsNullOrWhiteSpace(desktop) ? Environment.CurrentDirectory : desktop,
                SupportReportExporter.DefaultFileName());
        }

        var reportPath = SupportReportExporter.Export(output);
        Console.WriteLine($"Support report saved: {reportPath}");
        Console.WriteLine(
            "The report omits usernames, filesystem paths, network addresses, MAC addresses, and credentials.");
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
            DisplayWizardAdapter.IsManagedDeviceEnabled(0x00000008, 0) &&
            !DisplayWizardAdapter.IsManagedDeviceEnabled(0, 0) &&
            !DisplayWizardAdapter.IsManagedDeviceEnabled(0x00000008, 10) &&
            !DisplayWizardAdapter.IsManagedDeviceEnabled(0x00000008, 22) &&
            !DisplayWizardAdapter.IsManagedDeviceEnabled(0x00000008, 31),
            "Managed virtual display enabled-state classification failed.");
        var unmanagedSunshine = new SunshineBackendSnapshot(
            SunshineBackendKind.None,
            null,
            null,
            null,
            null,
            false);
        var disabledBackendState = BackendLifecycleStateStore.Create(
            BackendDesiredState.Disabled,
            restoreRecoveryTask: true,
            restoreRescueAgent: true,
            managedVirtualDisplayInstancesToRestore: [@"ROOT\DISPLAY\0001"],
            unmanagedSunshine);
        var disabledBackendComponents = new BackendLifecycleComponents(
            RecoveryTaskInstalled: false,
            RescueAgentTaskInstalled: false,
            RescueAgentRunning: false,
            RecoveryPending: false,
            ActivePhysicalDisplayCount: 1,
            ManagedVirtualDisplayInstalled: true,
            ManagedVirtualDisplayEnabled: false,
            ManagedVirtualDisplayActive: false,
            ManagedVirtualDisplayDevices:
            [
                new ManagedVddDeviceStatus(
                    @"ROOT\DISPLAY\0001",
                    Present: true,
                    Enabled: false,
                    DeviceStatus: 0,
                    ProblemCode: 22),
            ],
            new SunshineBackendObservation(
                SunshineBackendKind.None,
                Exists: false,
                Running: false,
                ServiceStartType: null,
                DelayedAutoStart: null,
                Error: null));
        Require(
            BackendLifecycleManager.Evaluate(
                disabledBackendState,
                disabledBackendComponents).Status ==
            BackendLifecycleStatus.Disabled,
            "A fully paused backend was not classified as Disabled.");
        Require(
            BackendLifecycleManager.Evaluate(
                disabledBackendState,
                disabledBackendComponents with
                {
                    ManagedVirtualDisplayEnabled = true,
                    ManagedVirtualDisplayDevices =
                    [
                        new ManagedVddDeviceStatus(
                            @"ROOT\DISPLAY\0001",
                            Present: true,
                            Enabled: true,
                            DeviceStatus: 8,
                            ProblemCode: 0),
                    ],
                }).Status == BackendLifecycleStatus.Partial,
            "A partially paused backend was not classified as Partial.");
        Require(
            BackendLifecycleManager.Evaluate(
                disabledBackendState,
                disabledBackendComponents,
                ["synthetic inspection error"]).Status ==
            BackendLifecycleStatus.Error,
            "A backend inspection failure was not classified as Error.");
        var statePayload =
            BackendLifecycleStateStore.SerializeStateForTest(
                disabledBackendState);
        Require(
            BackendLifecycleStateStore.ComputeSha256ForTest(statePayload) ==
            BackendLifecycleStateStore.ComputeSha256ForTest(statePayload),
            "Backend lifecycle integrity hashing is not deterministic.");
        Require(
            InstallerMaintenanceFence.IsMutatingCommand(
                "backend",
                ["enable"]) &&
            InstallerMaintenanceFence.IsMutatingCommand(
                "session",
                ["start"]) &&
            InstallerMaintenanceFence.IsMutatingCommand(
                "recovery",
                ["install"]) &&
            !InstallerMaintenanceFence.IsMutatingCommand(
                "backend",
                ["status"]) &&
            !InstallerMaintenanceFence.IsMutatingCommand(
                "agent",
                ["run", "--background"]),
            "Installer-maintenance mutation classification is unsafe.");
        Require(
            InstallerMaintenanceFence.RequiresSerializedAccessForTest(
                "backend",
                ["status", "--maintenance-owner-pid", "1234"],
                fencePresent: true) &&
            !InstallerMaintenanceFence.RequiresSerializedAccessForTest(
                "backend",
                ["status"],
                fencePresent: true) &&
            InstallerMaintenanceFence.RequiresSerializedAccessForTest(
                "backend",
                ["enable"],
                fencePresent: false) &&
            !InstallerMaintenanceFence.RequiresSerializedAccessForTest(
                "maintenance",
                ["begin", "--owner-pid", "1234"],
                fencePresent: true) &&
            !InstallerMaintenanceFence.RequiresSerializedAccessForTest(
                "maintenance",
                ["recovery-task-was-present", "--owner-pid", "1234"],
                fencePresent: true),
            "Owner-tagged status probes do not enter serialized maintenance access.");
        Require(
            InstallerMaintenanceFence.IsOwnerlessPhysicalRecoveryForTest(
                "session",
                ["recover"]) &&
            !InstallerMaintenanceFence.IsOwnerlessPhysicalRecoveryForTest(
                "session",
                ["start"]),
            "Exact physical recovery is not available safely during interrupted maintenance.");
        Require(
            InstallerMaintenanceFence
                .MustBootstrapBeforeProtectedStateForTest(
                    protectionInitialized: false) &&
            !InstallerMaintenanceFence
                .MustBootstrapBeforeProtectedStateForTest(
                    protectionInitialized: true),
            "Legacy installer maintenance can touch protected state before its typed recovery bootstrap.");
        Require(
            InstallerMaintenanceFence.ManagedVddBootstrapAllowedForTest(
                BackendPreferenceState.Enabled,
                existingBackendWasEnabled: null) &&
            !InstallerMaintenanceFence.ManagedVddBootstrapAllowedForTest(
                BackendPreferenceState.Disabled,
                existingBackendWasEnabled: null) &&
            InstallerMaintenanceFence.ManagedVddBootstrapAllowedForTest(
                BackendPreferenceState.Disabled,
                existingBackendWasEnabled: true) &&
            !InstallerMaintenanceFence.ManagedVddBootstrapAllowedForTest(
                BackendPreferenceState.Enabled,
                existingBackendWasEnabled: false),
            "Installer managed-VDD recovery does not preserve the durable enabled/paused snapshot across an interrupted upgrade.");
        var olderMaintenanceState = new InstallerMaintenanceState(
            1,
            1234,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            BackendWasEnabled: false,
            RescueAgentTaskWasPresent: false,
            RecoveryTaskWasPresent: false);
        var newerMaintenanceState = olderMaintenanceState with
        {
            OwnerProcessId = 5678,
            OwnerStartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30),
            BeganAtUtc = DateTimeOffset.UtcNow,
        };
        Require(
            InstallerMaintenanceFence.SelectNewestValidStateForTest(
                olderMaintenanceState,
                newerMaintenanceState,
                anyRecordExists: true) == newerMaintenanceState &&
            InstallerMaintenanceFence.CanRepairInterruptedFirstPublicationForTest(
                primaryExists: false,
                primary: null,
                backupExists: true,
                backup: null),
            "Installer-maintenance redundant selection or torn first-publication recovery failed.");
        Require(
            InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: true,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Present,
                ExactScheduledTaskState.Present) &&
            !InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: true,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Missing,
                ExactScheduledTaskState.Present) &&
            InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: false,
                backendWasEnabled: true,
                rescueAgentTaskWasPresent: true,
                recoveryTaskWasPresent: true,
                ExactScheduledTaskState.Missing,
                ExactScheduledTaskState.Missing) &&
            !InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: false,
                backendWasEnabled: false,
                rescueAgentTaskWasPresent: false,
                recoveryTaskWasPresent: false,
                ExactScheduledTaskState.Present,
                ExactScheduledTaskState.Missing) &&
            !InstallerMaintenanceFence.CanEndForTest(
                backendIsEnabled: true,
                backendWasEnabled: false,
                rescueAgentTaskWasPresent: false,
                recoveryTaskWasPresent: false,
                ExactScheduledTaskState.Missing,
                ExactScheduledTaskState.Missing),
            "Installer maintenance can clear without satisfying its durable safeguard obligations.");
        Require(
            BackendLifecycleStateStore.ClassifyUninstallMarkerForTest(null) ==
                UninstallTransactionStage.None &&
            BackendLifecycleStateStore.ClassifyUninstallMarkerForTest(
                "vita-moonlight-uninstall-in-progress-v1") ==
                UninstallTransactionStage.InProgress &&
            BackendLifecycleStateStore.ClassifyUninstallMarkerForTest(
                "vita-moonlight-uninstall-finalized-v1") ==
                UninstallTransactionStage.Finalized &&
            BackendLifecycleStateStore.ClassifyUninstallMarkerForTest(
                "torn") == UninstallTransactionStage.Invalid,
            "Uninstall transaction stage classification failed closed.");
        var managedSunshine = new SunshineBackendSnapshot(
            SunshineBackendKind.Service,
            "SunshineService",
            SunshineBackendController.ServiceAutoStart,
            true,
            null,
            true);
        Require(
            SunshineBackendController.MatchesDesiredState(
                BackendDesiredState.Disabled,
                managedSunshine,
                new SunshineBackendObservation(
                    SunshineBackendKind.Service,
                    Exists: true,
                    Running: false,
                    SunshineBackendController.ServiceDisabled,
                    DelayedAutoStart: true,
                    Error: null)) &&
            SunshineBackendController.MatchesDesiredState(
                BackendDesiredState.Enabled,
                managedSunshine,
                new SunshineBackendObservation(
                    SunshineBackendKind.Service,
                    Exists: true,
                    Running: true,
                    SunshineBackendController.ServiceAutoStart,
                    DelayedAutoStart: true,
                Error: null)),
            "Legacy Sunshine snapshot compatibility validation failed.");
        var uninstallHandoffState =
            BackendLifecycleStateStore.WithNextRevision(
                disabledBackendState with
                {
                    Sunshine = managedSunshine,
                },
                BackendDesiredState.Disabled,
                BackendLifecycleStatus.Partial,
                "temporary uninstall handoff");
        Require(
            uninstallHandoffState.DesiredState ==
                BackendDesiredState.Disabled &&
            uninstallHandoffState.RestoreRecoveryTask &&
            uninstallHandoffState.RestoreRescueAgent &&
            uninstallHandoffState.ManagedVirtualDisplayInstancesToRestore
                .SequenceEqual([@"ROOT\DISPLAY\0001"]) &&
            uninstallHandoffState.Sunshine == managedSunshine,
            "Uninstall handoff state lost the disabled intent or original restore snapshot.");
        var suspendIntent = new DisplaySuspendIntentState(
            1,
            Guid.NewGuid().ToString("N"),
            1234,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);
        Require(
            DisplaySuspendIntentStore.ClassifyForTest(
                recordExists: false,
                state: null,
                ownerIsAlive: null) ==
                DisplaySuspendIntentDisposition.Missing &&
            DisplaySuspendIntentStore.ClassifyForTest(
                recordExists: true,
                suspendIntent,
                ownerIsAlive: true) ==
                DisplaySuspendIntentDisposition.Active &&
            DisplaySuspendIntentStore.ClassifyForTest(
                recordExists: true,
                suspendIntent,
                ownerIsAlive: null) ==
                DisplaySuspendIntentDisposition.Active &&
            DisplaySuspendIntentStore.ClassifyForTest(
                recordExists: true,
                suspendIntent,
                ownerIsAlive: false) ==
                DisplaySuspendIntentDisposition.Stale &&
            DisplaySuspendIntentStore.ClassifyForTest(
                recordExists: true,
                state: null,
                ownerIsAlive: false) ==
                DisplaySuspendIntentDisposition.Stale,
            "Windows suspend intent classification did not fail physical-safe.");
        Require(
            !DisplaySuspendIntentStore.BlocksDisplayMutationForTest(
                DisplaySuspendIntentDisposition.Missing,
                DisplaySuspendIntentDisposition.Missing) &&
            DisplaySuspendIntentStore.BlocksDisplayMutationForTest(
                DisplaySuspendIntentDisposition.Missing,
                DisplaySuspendIntentDisposition.Active) &&
            DisplaySuspendIntentStore.BlocksDisplayMutationForTest(
                DisplaySuspendIntentDisposition.Active,
                DisplaySuspendIntentDisposition.Missing) &&
            DisplaySuspendIntentStore.BlocksDisplayMutationForTest(
                DisplaySuspendIntentDisposition.Stale,
                DisplaySuspendIntentDisposition.Missing),
            "A Windows suspend transition can escape the before/after display-lease fence.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 1,
                activeManagedVirtualPaths: 0,
                pendingTransactionAtWake: null,
                currentPendingTransaction: null,
                stableSamples: 2,
                minimumRecoveryAgeReached: false) ==
            ResumeTopologyDecision.Healthy,
            "A stable physical-only resume topology was not accepted as healthy.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 0,
                activeManagedVirtualPaths: 1,
                pendingTransactionAtWake: null,
                currentPendingTransaction: null,
                stableSamples: 3,
                minimumRecoveryAgeReached: true) ==
            ResumeTopologyDecision.Recover,
            "A stable managed-VDD-only resume topology was not recovered.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 1,
                activeManagedVirtualPaths: 1,
                pendingTransactionAtWake: null,
                currentPendingTransaction: null,
                stableSamples: 3,
                minimumRecoveryAgeReached: true) ==
            ResumeTopologyDecision.Recover,
            "A stable mixed physical/VDD resume topology was not recovered.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 1,
                activeManagedVirtualPaths: 0,
                pendingTransactionAtWake: null,
                currentPendingTransaction: null,
                stableSamples: 2,
                minimumRecoveryAgeReached: true) ==
            ResumeTopologyDecision.Healthy,
            "An unrelated virtual display affected physical-only resume classification.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 1,
                activeManagedVirtualPaths: 0,
                pendingTransactionAtWake: "same-transaction",
                currentPendingTransaction: "same-transaction",
                stableSamples: 3,
                minimumRecoveryAgeReached: true) ==
            ResumeTopologyDecision.Recover,
            "An interrupted pre-sleep display transaction was not recovered.");
        Require(
            ResumeTopologyClassifier.Classify(
                activePhysicalPaths: 0,
                activeManagedVirtualPaths: 1,
                pendingTransactionAtWake: null,
                currentPendingTransaction: "new-transaction",
                stableSamples: 3,
                minimumRecoveryAgeReached: true) ==
            ResumeTopologyDecision.Healthy,
            "A new post-resume streaming transaction was incorrectly recovered.");
        Require(
            ExactScheduledTaskManager.IsNotFound(
                new InvalidOperationException(
                    "wrapped task lookup failure",
                    new COMException(
                        "missing task",
                        unchecked((int)0x80070003)))) &&
            !ExactScheduledTaskManager.IsNotFound(
                new UnauthorizedAccessException(
                    "task access denied")),
            "Exact scheduled-task missing/error classification failed.");
        RunOwnedStateCleanupSelfTest();
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
        Require(
            command.Contains("session hook-start", StringComparison.Ordinal) &&
            command.Contains("%SUNSHINE_CLIENT_WIDTH%", StringComparison.Ordinal) &&
            !command.Contains(" session start ", StringComparison.Ordinal),
            "Sunshine tolerant hook generation failed.");
        Require(
            TryDescribeUnrelatedSunshineHook(
                "session",
                ["hook-start", "--width", "1920", "--height", "1080", "--fps", "120"],
                out var ignoredHook) &&
            ignoredHook.Contains("left unchanged", StringComparison.OrdinalIgnoreCase),
            "An unrelated Sunshine client would not bypass host display state safely.");
        Require(!TryDescribeUnrelatedSunshineHook(
                "session",
                ["hook-start", "--width", "960", "--height", "544", "--fps", "60"],
                out _),
            "A recognized Vita client was incorrectly classified as an unrelated no-op.");

        var sunshineTestDirectory = Path.Combine(Path.GetTempPath(), $"vita-moonlight-self-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sunshineTestDirectory);
            using var ownershipJournalTest = SunshineOwnershipJournal.UseTestFile(
                Path.Combine(sunshineTestDirectory, "ownership-test.json"));
            var hostOwnershipClassification = new SunshineOwnershipState();
            SunshineOwnershipJournal.GetOrAddLocation(
                hostOwnershipClassification,
                Path.Combine(sunshineTestDirectory, "Apollo"),
                "apollo");
            Require(
                SunshineOwnershipJournal.HasLocationForHost(
                    hostOwnershipClassification,
                    "apollo") &&
                !SunshineOwnershipJournal.HasLocationForHost(
                    hostOwnershipClassification,
                    "sunshine"),
                "Streaming-host ownership could not isolate experimental Apollo cleanup from Sunshine cleanup.");

            var invalidConfigurationDirectory = Path.Combine(
                sunshineTestDirectory,
                "invalid-preflight");
            Directory.CreateDirectory(invalidConfigurationDirectory);
            File.WriteAllText(
                Path.Combine(invalidConfigurationDirectory, "apps.json"),
                "{ invalid json");
            var invalidConfigurationRejected = false;
            try
            {
                SunshineConfigurator.Configure(
                    HostSettings.Default with
                    {
                        SunshineConfigDirectory = invalidConfigurationDirectory,
                        IntegrateAllSunshineApps = false,
                    },
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            }
            catch (JsonException)
            {
                invalidConfigurationRejected = true;
            }
            Require(
                invalidConfigurationRejected &&
                !File.Exists(Path.Combine(
                    invalidConfigurationDirectory,
                    "apps.json.vita-moonlight.backup")),
                "A failed configuration preflight left an unowned apps.json backup.");

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
            File.WriteAllText(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                """
                unrelated = preserved
                min_log_level = info
                output_name = {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}
                global_prep_cmd = [{"do":"user-prep","undo":"user-undo","elevated":false,"extension":{"value":7}}]
                """);
            var priorInfoLoggingOwnership = new SunshineOwnershipState();
            var priorInfoLoggingLocation = SunshineOwnershipJournal
                .GetOrAddLocation(
                    priorInfoLoggingOwnership,
                    sunshineTestDirectory,
                    "sunshine");
            priorInfoLoggingLocation.Values["min_log_level"] =
                new SunshineOwnedValue(
                    OriginalPresent: true,
                    OriginalValue: "warning",
                    AppliedValue: "info");
            SunshineOwnershipJournal.Save(priorInfoLoggingOwnership);
            var integrationSettings = HostSettings.Default with { SunshineConfigDirectory = sunshineTestDirectory };
            var integrationResult = SunshineConfigurator.Configure(integrationSettings, @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            Require(integrationResult.CoveredApplicationCount == 2, "Every-app Sunshine integration count failed.");
            Require(integrationResult.UsesAuthenticatedStreamBoundary, "Authenticated Vita stream-boundary integration was not selected.");
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
            var nativeConfiguration = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            Require(nativeConfiguration.Count(line =>
                        line.StartsWith("output_name =", StringComparison.OrdinalIgnoreCase)) == 1 &&
                    nativeConfiguration.Single(line =>
                            line.StartsWith("output_name =", StringComparison.OrdinalIgnoreCase))
                        .Split('=', 2)[1].Trim().Length == 0,
                "Sunshine retained a pinned output instead of selecting the active display after handoff.");
            Require(!nativeConfiguration.Any(line =>
                    line.StartsWith("dd_resolution_option =", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("dd_refresh_rate_option =", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("dd_manual_refresh_rate =", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("dd_mode_remapping =", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("dd_hdr_option =", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("dd_config_revert_delay =", StringComparison.OrdinalIgnoreCase)),
                "Sunshine retained Vita-owned native display directives.");
            Require(nativeConfiguration.Contains("dd_configuration_option = disabled") &&
                    nativeConfiguration.Contains("dd_config_revert_on_disconnect = disabled"),
                "Sunshine native display management was not disabled for companion-owned handoff.");
            Require(nativeConfiguration.Contains("min_log_level = warning") &&
                    !SunshineOwnershipJournal.Load().Locations.Single()
                        .Values.ContainsKey("min_log_level"),
                "Authenticated handoff did not restore and retire the prior Vita-owned Sunshine INFO log level.");
            var globalPrepLine = nativeConfiguration.Single(line =>
                line.StartsWith("global_prep_cmd =", StringComparison.OrdinalIgnoreCase));
            var globalPrep = JsonNode.Parse(
                globalPrepLine[(globalPrepLine.IndexOf('=') + 1)..].Trim())!.AsArray();
            Require(globalPrep.Count == 1 &&
                    globalPrep[0]!["do"]!.GetValue<string>() == "user-prep" &&
                    globalPrep[0]!["extension"]!["value"]!.GetValue<int>() == 7,
                "Authenticated handoff changed an unrelated Sunshine global preparation entry.");
            Require(SunshineConfigurator.IsAuthenticatedStreamBoundaryConfigurationReady(
                    sunshineTestDirectory,
                    integrationSettings.ForceSdr),
                "Authenticated stream-boundary Sunshine configuration readiness failed.");
            var staleGlobalCommands = globalPrep.DeepClone().AsArray();
            staleGlobalCommands.Add(new JsonObject
            {
                ["do"] = SunshineConfigurator.BuildStartCommand(
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                ["undo"] = SunshineConfigurator.BuildStopCommand(
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                ["elevated"] = true,
            });
            File.WriteAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                nativeConfiguration.Select(line => line.StartsWith(
                        "global_prep_cmd =",
                        StringComparison.OrdinalIgnoreCase)
                    ? $"global_prep_cmd = {staleGlobalCommands.ToJsonString()}"
                    : line));
            Require(!SunshineConfigurator.IsAuthenticatedStreamBoundaryConfigurationReady(
                    sunshineTestDirectory,
                    integrationSettings.ForceSdr),
                "Sunshine readiness accepted an unsafe stale global Vita display hook.");
            File.WriteAllLines(Path.Combine(sunshineTestDirectory, "sunshine.conf"), nativeConfiguration);

            var readinessKeys = new[]
            {
                "dd_configuration_option = disabled",
                "dd_config_revert_on_disconnect = disabled",
                "controller = enabled",
                "gamepad = auto",
                "motion_as_ds4 = enabled",
                "touchpad_as_ds4 = enabled",
                "keyboard = enabled",
                "mouse = enabled",
                "native_pen_touch = enabled",
            };
            foreach (var readinessKey in readinessKeys)
            {
                File.WriteAllLines(
                    Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                    nativeConfiguration.Where(line => !string.Equals(
                        line,
                        readinessKey,
                        StringComparison.OrdinalIgnoreCase)));
                Require(!SunshineConfigurator.IsAuthenticatedStreamBoundaryConfigurationReady(
                        sunshineTestDirectory,
                        integrationSettings.ForceSdr),
                    $"Sunshine readiness accepted missing owned key '{readinessKey}'.");
            }
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
            var staleInventoryAt = DateTimeOffset.Now.AddMinutes(-10);
            var freshInventoryAt = DateTimeOffset.Now;
            var generationLogPath = Path.Combine(
                sunshineTestDirectory,
                "sunshine-generation.log");
            File.WriteAllText(generationLogPath, $$"""
                [{{staleInventoryAt:yyyy-MM-dd HH:mm:ss.fff}}]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{77777777-7777-7777-7777-777777777777}",
                    "friendly_name": "VDD by MTT"
                  }
                ]
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    generationLogPath,
                    null,
                    freshInventoryAt.AddMinutes(-1)) is null,
                "Sunshine display selection accepted inventory from before the current driver/service generation.");
            File.AppendAllText(generationLogPath, $$"""

                [{{freshInventoryAt:yyyy-MM-dd HH:mm:ss.fff}}]: Info: Currently available display devices:
                [
                  {
                    "device_id": "{88888888-8888-8888-8888-888888888888}",
                    "friendly_name": "VDD by MTT"
                  }
                ]
                """);
            Require(SunshineConfigurator.FindManagedDisplayDeviceId(
                    generationLogPath,
                    null,
                    freshInventoryAt.AddSeconds(-1)) ==
                    "{88888888-8888-8888-8888-888888888888}",
                "Sunshine display selection rejected fresh current-generation inventory.");
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

            SunshineConfigurator.Configure(
                integrationSettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var reconfiguredLines = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            var reconfiguredGlobalLine = reconfiguredLines.Single(line =>
                line.StartsWith("global_prep_cmd =", StringComparison.OrdinalIgnoreCase));
            var reconfiguredGlobalPrep = JsonNode.Parse(
                reconfiguredGlobalLine[(reconfiguredGlobalLine.IndexOf('=') + 1)..]
                    .Trim())!.AsArray();
            Require(reconfiguredGlobalPrep.Count == 1 &&
                    reconfiguredGlobalPrep.Count(command =>
                        command?["do"]?.GetValue<string>().Contains(
                            "VitaMoonlight.Host",
                            StringComparison.OrdinalIgnoreCase) == true) == 0,
                "Sunshine reconfiguration installed an unsafe global display hook.");

            var cleanupResult = SunshineConfigurator.RemoveManagedIntegration();
            Require(!cleanupResult.RemovedGeneratedApplication,
                "Authenticated all-app configuration unexpectedly created a disposable Sunshine application.");
            Require(cleanupResult.RemovedHooks == 0,
                "Authenticated all-app cleanup unexpectedly removed a Sunshine hook.");
            Require(cleanupResult.RemovedNativeDisplaySettings,
                "Uninstall cleanup retained owned Sunshine settings.");
            Require(!File.Exists(Path.Combine(
                    sunshineTestDirectory,
                    "apps.json.vita-moonlight.backup")),
                "Uninstall cleanup retained the Vita Sunshine backup.");
            using var cleanedApps = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")));
            Require(!cleanedApps.RootElement.GetProperty("apps").EnumerateArray().Any(app =>
                    app.GetProperty("name").GetString() == "Vita Moonlight"),
                "Authenticated all-app configuration created a redundant Vita application.");
            var cleanedSteam = cleanedApps.RootElement.GetProperty("apps").EnumerateArray().First(app =>
                app.GetProperty("name").GetString() == "Steam Big Picture");
            Require(cleanedSteam.GetProperty("prep-cmd").EnumerateArray().Any(prep =>
                    prep.GetProperty("do").GetString() == "steam://open/bigpicture"),
                "Uninstall cleanup removed a user-owned Sunshine preparation command.");
            var cleanedConfiguration = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            var cleanedGlobalLine = cleanedConfiguration.Single(line =>
                line.StartsWith("global_prep_cmd =", StringComparison.OrdinalIgnoreCase));
            var cleanedGlobalPrep = JsonNode.Parse(
                cleanedGlobalLine[(cleanedGlobalLine.IndexOf('=') + 1)..]
                    .Trim())!.AsArray();
            Require(cleanedGlobalPrep.Count == 1 &&
                    cleanedGlobalPrep[0]?["do"]?.GetValue<string>() == "user-prep" &&
                    cleanedGlobalPrep[0]?["extension"]?["value"]?.GetValue<int>() == 7,
                "Uninstall cleanup changed or removed an unrelated global preparation entry.");
            Require(!cleanedConfiguration.Any(line =>
                    line.StartsWith("dd_configuration_option =", StringComparison.OrdinalIgnoreCase)),
                "Uninstall cleanup retained a newly added Sunshine display setting.");
            Require(!cleanedConfiguration.Any(line =>
                    line.StartsWith("controller =", StringComparison.OrdinalIgnoreCase)),
                "Uninstall cleanup retained a newly added Sunshine controller setting.");
            Require(cleanedConfiguration.Contains("min_log_level = warning"),
                "Uninstall cleanup changed the user's Sunshine log level.");
            Require(cleanedConfiguration.Contains(
                    "output_name = {AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}"),
                "Uninstall cleanup did not restore the user's prior Sunshine output selection.");
            var secondCleanup = SunshineConfigurator.RemoveManagedIntegration();
            Require(
                secondCleanup.RemovedHooks == 0 &&
                !secondCleanup.RemovedGeneratedApplication &&
                !secondCleanup.RemovedNativeDisplaySettings,
                "Sunshine uninstall cleanup was not idempotent.");

            var falseBaselineLines = new List<string>
            {
                "unrelated = preserved",
            };
            SunshineConfigurator.ConfigureNativeDisplayManagement(
                falseBaselineLines,
                "{11111111-2222-3333-4444-555555555555}",
                forceSdr: true);
            var falseBaselineState = new SunshineOwnershipState
            {
                FormatVersion = SunshineOwnershipJournal.LegacyFormatVersion,
            };
            var falseBaselineOwnership =
                SunshineOwnershipJournal.GetOrAddLocation(
                    falseBaselineState,
                    sunshineTestDirectory,
                    "sunshine");
            foreach (var line in falseBaselineLines.Where(line =>
                         line.StartsWith("output_name =", StringComparison.OrdinalIgnoreCase) ||
                         line.StartsWith("dd_", StringComparison.OrdinalIgnoreCase)))
            {
                var separator = line.IndexOf('=');
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                falseBaselineOwnership.Values[key] =
                    new SunshineOwnedValue(true, value, value);
            }
            var currentModeRemapping =
                falseBaselineOwnership.Values["dd_mode_remapping"].AppliedValue;
            const string legacyMalformedModeRemapping =
                "{\"mixed\":[],\"resolution_only\":[" +
                "{\"requested_resolution\":\"960x540\",\"final_resolution\":\"960x540\"}," +
                "{\"requested_resolution\":\"960x544\",\"final_resolution\":\"960x544\"}," +
                "{\"requested_resolution\":\"1280x720\",\"final_resolution\":\"1280x720\"}," +
                "{\"final_resolution\":\"960x544\"}]," +
                "\"refresh_rate_only\":[]}";
            falseBaselineOwnership.Values["dd_mode_remapping"] =
                new SunshineOwnedValue(
                    true,
                    legacyMalformedModeRemapping,
                    currentModeRemapping);
            var cleanupFalseBaselineState =
                SunshineOwnershipJournal.Clone(falseBaselineState);
            var incompleteFalseBaselineState =
                SunshineOwnershipJournal.Clone(falseBaselineState);
            Require(SunshineConfigurator.MigrateLegacyFalseBaselineOwnership(
                    falseBaselineState,
                    falseBaselineOwnership,
                    falseBaselineLines) &&
                    falseBaselineOwnership.Values.Values.All(value =>
                        !value.OriginalPresent && value.OriginalValue is null),
                "A complete v1 Vita display fingerprint was not reclassified from a false baseline.");
            var incompleteOwnership =
                incompleteFalseBaselineState.Locations.Single();
            incompleteOwnership.Values["dd_manual_refresh_rate"] =
                new SunshineOwnedValue(true, "59", "59");
            Require(!SunshineConfigurator.MigrateLegacyFalseBaselineOwnership(
                        incompleteFalseBaselineState,
                        incompleteOwnership,
                        falseBaselineLines) &&
                    incompleteOwnership.Values.Values.All(value =>
                        value.OriginalPresent),
                "An incomplete v1 display fingerprint was incorrectly claimed as Vita-owned.");

            File.WriteAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                falseBaselineLines);
            SunshineOwnershipJournal.Save(cleanupFalseBaselineState);
            var falseBaselineCleanup =
                SunshineConfigurator.RemoveManagedIntegration();
            var falseBaselineCleanedLines = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            Require(falseBaselineCleanup.RemovedNativeDisplaySettings &&
                    falseBaselineCleanedLines.SequenceEqual(
                        new[] { "unrelated = preserved" }),
                "Uninstall restored a false v1 Vita display baseline instead of removing it.");

            File.WriteAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                falseBaselineLines);
            SunshineOwnershipJournal.Save(
                SunshineOwnershipJournal.Clone(cleanupFalseBaselineState));
            SunshineConfigurator.Configure(
                integrationSettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var upgradedFalseBaselineLines = File.ReadAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"));
            var upgradedFalseBaselineOwnership =
                SunshineOwnershipJournal.Load();
            Require(
                upgradedFalseBaselineOwnership.FormatVersion ==
                    SunshineOwnershipJournal.CurrentFormatVersion &&
                upgradedFalseBaselineOwnership.Locations.Single()
                    .LegacyDisplayOwnershipMigrationCompleted == true &&
                upgradedFalseBaselineLines.Any(line =>
                    line.StartsWith("output_name =", StringComparison.OrdinalIgnoreCase) &&
                    line.Split('=', 2)[1].Trim().Length == 0) &&
                upgradedFalseBaselineLines.Contains(
                    "dd_configuration_option = disabled") &&
                !upgradedFalseBaselineLines.Any(line =>
                    line.StartsWith("dd_mode_remapping =", StringComparison.OrdinalIgnoreCase)),
                "Setup did not migrate the live v1 false baseline to companion-owned global handoff.");
            SunshineConfigurator.RemoveManagedIntegration();
            Require(File.ReadAllLines(
                    Path.Combine(sunshineTestDirectory, "sunshine.conf"))
                    .SequenceEqual(new[] { "unrelated = preserved" }),
                "Cleanup after v1 setup migration restored retired native display settings.");

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
                                "cmd.exe /D /S /C \"\"C:\\Program Files\\Vita Moonlight Host\\" +
                                "VitaMoonlight.Host.exe\" session start " +
                                "--width %SUNSHINE_CLIENT_WIDTH% " +
                                "--height %SUNSHINE_CLIENT_HEIGHT% " +
                                "--fps %SUNSHINE_CLIENT_FPS%\"",
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
            Require(File.ReadAllLines(
                    Path.Combine(sunshineTestDirectory, "sunshine.conf"))
                    .Contains("min_log_level = info"),
                "Explicit legacy hook mode did not retain its INFO log fallback.");
            Require(SunshineConfigurator.IsManagedHookReady(
                    sunshineTestDirectory,
                    legacySettings.SunshineApplicationName,
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                "Manual Sunshine hook readiness rejected an exact owned hook.");
            var upgradedLegacyRoot = JsonNode.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")))!.AsObject();
            var upgradedLegacyHooks = upgradedLegacyRoot["apps"]!.AsArray()
                .OfType<JsonObject>()
                .Single(app => app["name"]?.GetValue<string>() == "Vita Moonlight")
                ["prep-cmd"]!.AsArray()
                .OfType<JsonObject>()
                .ToArray();
            Require(
                upgradedLegacyHooks.Length == 1 &&
                upgradedLegacyHooks[0]["do"]?.GetValue<string>()?.Contains(
                    "session hook-start",
                    StringComparison.Ordinal) == true,
                "Legacy strict Sunshine hook was not replaced by the tolerant boundary.");
            var hookRemovedForReadiness = upgradedLegacyRoot.DeepClone().AsObject();
            hookRemovedForReadiness["apps"]!.AsArray()
                .OfType<JsonObject>()
                .Single(app => app["name"]?.GetValue<string>() == "Vita Moonlight")
                .Remove("prep-cmd");
            File.WriteAllText(
                Path.Combine(sunshineTestDirectory, "apps.json"),
                hookRemovedForReadiness.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
            Require(!SunshineConfigurator.IsManagedHookReady(
                    sunshineTestDirectory,
                    legacySettings.SunshineApplicationName,
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                "Manual Sunshine hook readiness accepted a missing on-disk hook.");
            File.WriteAllText(
                Path.Combine(sunshineTestDirectory, "apps.json"),
                upgradedLegacyRoot.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
            var savedHookOwnership = SunshineOwnershipJournal.Load();
            SunshineOwnershipJournal.Delete();
            Require(!SunshineConfigurator.IsManagedHookReady(
                    sunshineTestDirectory,
                    legacySettings.SunshineApplicationName,
                    @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                "Manual Sunshine hook readiness accepted a hook without an exact ownership record.");
            SunshineOwnershipJournal.Save(savedHookOwnership);
            var legacyCleanup = SunshineConfigurator.RemoveManagedIntegration();
            Require(
                legacyCleanup.RemovedHooks == 1 &&
                legacyCleanup.RemovedGeneratedApplication,
                "Exact owned hook/application cleanup failed.");

            SunshineConfigurator.Configure(
                legacySettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            SunshineConfigurator.Configure(
                integrationSettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var nativeTransitionRoot = JsonNode.Parse(
                File.ReadAllText(Path.Combine(sunshineTestDirectory, "apps.json")))!.AsObject();
            Require(!nativeTransitionRoot["apps"]!.AsArray()
                    .OfType<JsonObject>()
                    .Any(app => string.Equals(
                        app["name"]?.GetValue<string>(),
                        "Vita Moonlight",
                        StringComparison.OrdinalIgnoreCase)),
                "Switching from manual hooks to global all-app mode retained the unchanged generated launcher.");
            var nativeTransitionCleanup =
                SunshineConfigurator.RemoveManagedIntegration();
            Require(!nativeTransitionCleanup.RemovedGeneratedApplication,
                "Native-mode cleanup claimed ownership of a generated launcher already removed during transition.");

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

            SunshineConfigurator.Configure(
                integrationSettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var editedGlobalLines = File.ReadAllLines(
                    Path.Combine(sunshineTestDirectory, "sunshine.conf"))
                .ToList();
            var editedGlobalCommands = new JsonArray
            {
                new JsonObject
                {
                    ["do"] = "user-prep",
                    ["undo"] = "user-undo",
                    ["elevated"] = false,
                    ["extension"] = new JsonObject { ["value"] = 7 },
                },
                new JsonObject
                {
                    ["do"] = SunshineConfigurator.BuildStartCommand(
                        @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                    ["undo"] = SunshineConfigurator.BuildStopCommand(
                        @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe"),
                    ["elevated"] = true,
                    ["user-note"] = "old modified Vita hook",
                },
            };
            editedGlobalLines.Add(
                $"global_prep_cmd = {editedGlobalCommands.ToJsonString()}");
            File.WriteAllLines(
                Path.Combine(sunshineTestDirectory, "sunshine.conf"),
                editedGlobalLines);
            SunshineConfigurator.Configure(
                integrationSettings,
                @"C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe");
            var preservedGlobalLine = File.ReadAllLines(
                    Path.Combine(sunshineTestDirectory, "sunshine.conf"))
                .Single(line => line.StartsWith(
                    "global_prep_cmd =",
                    StringComparison.OrdinalIgnoreCase));
            var migratedGlobalCommands = JsonNode.Parse(
                preservedGlobalLine[(preservedGlobalLine.IndexOf('=') + 1)..]
                    .Trim())!.AsArray();
            Require(migratedGlobalCommands.Count == 1 &&
                    migratedGlobalCommands[0]?["do"]?.GetValue<string>() ==
                        "user-prep" &&
                    migratedGlobalCommands[0]?["extension"]?["value"]?
                        .GetValue<int>() == 7,
                "Upgrade did not remove the obsolete Vita global hook while preserving an unrelated global command.");
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
        var testedStreamModeCount = 0;
        foreach (var desktopMode in VitaDisplayModes.Supported)
        {
            foreach (var streamFps in VitaDisplayModes.SupportedStreamFrameRates)
            {
                var streamMode = VitaDisplayModes.RequireSupportedStreamMode(
                    desktopMode.Width,
                    desktopMode.Height,
                    streamFps);
                Require(
                    streamMode.DesktopMode == desktopMode &&
                    streamMode.DesktopMode.Fps == VitaDisplayModes.DesktopRefreshRate &&
                    streamMode.StreamFps == streamFps,
                    $"Vita stream mode contract failed for {desktopMode.Width}x{desktopMode.Height} at {streamFps} FPS.");
                Require(VitaDisplayModes.TryGetSupportedStreamMode(
                        desktopMode.Width,
                        desktopMode.Height,
                        streamFps,
                        out var hookMode) &&
                    hookMode == streamMode,
                    $"Tolerant hook classification missed {streamMode}.");
                testedStreamModeCount++;
            }
        }
        Require(testedStreamModeCount == 15,
            "The exhaustive Vita resolution/frame-rate contract matrix was incomplete.");
        foreach (var invalidStreamMode in new[]
        {
            (Width: 800, Height: 600, Fps: 60),
            (Width: 1920, Height: 1080, Fps: 60),
            (Width: 960, Height: 544, Fps: 23),
            (Width: 960, Height: 544, Fps: 25),
            (Width: 960, Height: 544, Fps: 61),
        })
        {
            Require(!VitaDisplayModes.TryGetSupportedStreamMode(
                    invalidStreamMode.Width,
                    invalidStreamMode.Height,
                    invalidStreamMode.Fps,
                    out _),
                $"Tolerant hook would mutate the display for unrelated mode {invalidStreamMode}.");
            try
            {
                VitaDisplayModes.RequireSupportedStreamMode(
                    invalidStreamMode.Width,
                    invalidStreamMode.Height,
                    invalidStreamMode.Fps);
                throw new InvalidOperationException(
                    $"Unsupported stream mode {invalidStreamMode} was accepted.");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Expected: stream hooks accept only the public Vita contract.
            }
        }
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
            "Legacy display-mode hotkey mapping failed.");
        Require(
            !HostRecoveryAgentManager.LegacyModeHotkeysRequired(HostSettings.Default) &&
            HostRecoveryAgentManager.LegacyModeHotkeysRequired(
                HostSettings.Default with { IntegrateAllSunshineApps = false }) &&
            HostRecoveryAgentManager.LegacyModeHotkeysRequired(
                HostSettings.Default with { HostMode = "apollo" }),
            "Legacy display-mode hotkey policy does not match the selected host path.");
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
        Require(
            DisplayTopologyService.IsExactVitaVirtualDisplay(
                new DisplayDescriptor(0, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", true, true)) &&
            !DisplayTopologyService.IsExactVitaVirtualDisplay(
                new DisplayDescriptor(0, "VDD by MTT MTT1337", @"\\?\DISPLAY#THIRDPARTY#1", true, true)),
            "Vita topology authority was not bound exclusively to the exact MTT1337 monitor path.");
        var safeVirtualSelection = DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
        {
            new DisplayDescriptor(0, "MTT Office Monitor", @"\\?\DISPLAY#PHYSICAL#1", true, true),
            new DisplayDescriptor(1, "VDD by MTT", @"\\?\DISPLAY#MTT1337#1", false, true),
        }, "MTT");
        Require(safeVirtualSelection?.FriendlyName == "VDD by MTT",
            "A configured virtual-display match was allowed to select a physical monitor.");
        Require(DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Apollo Virtual Display",
                    @"\\?\DISPLAY#APOLLO#1",
                    true,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            }, null) is null,
            "Automatic virtual-display selection was allowed to hijack an unrelated VDD.");
        Require(DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Apollo Virtual Display",
                    @"\\?\DISPLAY#APOLLO#1",
                    true,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
                new DisplayDescriptor(
                    1,
                    "VDD by MTT",
                    @"\\?\DISPLAY#MTT1337#1",
                    false,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            }, null)?.FriendlyName == "VDD by MTT",
            "Automatic virtual-display selection did not choose the managed VDD in a multi-VDD topology.");
        Require(DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Legacy IddSampleDriver",
                    @"\\?\DISPLAY#LEGACYIDD#1",
                    false,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
                new DisplayDescriptor(
                    1,
                    "VDD by MTT",
                    @"\\?\DISPLAY#MTT1337#1",
                    false,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            }, "MTT1337") is null,
            "Exact Vita selection did not fail closed beside a competing legacy managed target.");
        Require(DisplayTopologyService.SelectVirtualDisplayForActivation(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Apollo Virtual Display",
                    @"\\?\DISPLAY#APOLLO#1",
                    false,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
                new DisplayDescriptor(
                    1,
                    "VDD by MTT",
                    @"\\?\DISPLAY#MTT1337#1",
                    false,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            }, "APOLLO")?.DevicePath.Contains("MTT1337", StringComparison.OrdinalIgnoreCase) == true,
            "The supported Sunshine selector honored arbitrary display-match text instead of exact Vita authority.");
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
        var managedOnlyRecoveryPath =
            DisplayTopologyService.SelectExactManagedVddOnlyRecoveryPath(
            [
                new DisplayDescriptor(
                    0,
                    "VDD by MTT",
                    @"\\?\DISPLAY#MTT1337#1",
                    true,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            ]);
        Require(
            managedOnlyRecoveryPath?.FriendlyName == "VDD by MTT" &&
            DisplayTopologyService.SelectExactManagedVddOnlyRecoveryPath(
            [
                managedOnlyRecoveryPath!,
                new DisplayDescriptor(
                    1,
                    "Physical Monitor",
                    @"\\?\DISPLAY#PHYSICAL#1",
                    true,
                    true,
                    5),
            ]) is null &&
            DisplayTopologyService.SelectExactManagedVddOnlyRecoveryPath(
            [
                new DisplayDescriptor(
                    0,
                    "Apollo Virtual Display",
                    @"\\?\DISPLAY#APOLLO#1",
                    true,
                    true,
                    WindowsDisplayNative.OutputTechnologyIndirectVirtual),
            ]) is null &&
            DisplayTopologyService.SelectExactManagedVddOnlyRecoveryPath(
            [
                managedOnlyRecoveryPath!,
                managedOnlyRecoveryPath! with
                {
                    PathIndex = 1,
                    DevicePath = @"\\?\DISPLAY#MTT1337#2",
                },
            ]) is null,
            "The old-install VDD-only recovery gate would touch a physical, unrelated, or ambiguous display topology.");
        Require(
            DisplayWizardAdapter.HasSingleManagedVddRestartTargetForTest(
            [
                new ManagedVddDeviceStatus(
                    @"ROOT\DISPLAY\0001",
                    Present: true,
                    Enabled: true,
                    DeviceStatus: 8,
                    ProblemCode: 0),
            ]) &&
            !DisplayWizardAdapter.HasSingleManagedVddRestartTargetForTest(
            [
                new ManagedVddDeviceStatus(
                    @"ROOT\DISPLAY\0001",
                    Present: true,
                    Enabled: true,
                    DeviceStatus: 8,
                    ProblemCode: 0),
                new ManagedVddDeviceStatus(
                    @"ROOT\DISPLAY\0002",
                    Present: true,
                    Enabled: true,
                    DeviceStatus: 8,
                    ProblemCode: 0),
            ]) &&
            !DisplayWizardAdapter.HasSingleManagedVddRestartTargetForTest(
            [
                new ManagedVddDeviceStatus(
                    @"ROOT\DISPLAY\0001",
                    Present: true,
                    Enabled: false,
                    DeviceStatus: 0,
                    ProblemCode: 22),
            ]),
            "Managed-VDD recovery target selection could restart a paused or shared/ambiguous device instance.");
        var transientSuspendDisplays =
            DisplayTopologyService.SelectPhysicalDisplaysForRecovery(new[]
            {
                new DisplayDescriptor(
                    0,
                    "Internal Panel",
                    @"\\?\DISPLAY#INTERNAL#1",
                    true,
                    false),
                new DisplayDescriptor(
                    1,
                    "Dock Monitor",
                    @"\\?\DISPLAY#DOCK#1",
                    false,
                    false),
                new DisplayDescriptor(
                    2,
                    "VDD by MTT",
                    @"\\?\DISPLAY#MTT1337#1",
                    true,
                    true),
            });
        Require(
            transientSuspendDisplays.Length == 1 &&
            transientSuspendDisplays[0].FriendlyName == "Internal Panel",
            "Suspend recovery discarded an active physical path while TargetAvailable was transiently false.");
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
        Require(
            DisplayTopologyService.IsPotentialPhysicalModeDrift(
                new AdvertisedDisplayMode(960, 544, 60)) &&
            DisplayTopologyService.IsPotentialPhysicalModeDrift(
                new AdvertisedDisplayMode(2560, 1440, 30)) &&
            !DisplayTopologyService.IsPotentialPhysicalModeDrift(
                new AdvertisedDisplayMode(2560, 1440, 280)),
            "Physical-mode repair did not gate registry reads to suspicious fallback/low-refresh modes.");
        Require(
            DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(960, 544, 60),
                new AdvertisedDisplayMode(2560, 1440, 144)) &&
            DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(800, 600, 60),
                new AdvertisedDisplayMode(1920, 1080, 60)) &&
            DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(1920, 1080, 30),
                new AdvertisedDisplayMode(1920, 1080, 60)),
            "Clear Vita/fallback physical-mode drift was not selected for repair.");
        Require(
            !DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(1280, 720, 60),
                new AdvertisedDisplayMode(2560, 1440, 144)) &&
            !DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(1920, 1080, 60),
                new AdvertisedDisplayMode(2560, 1440, 144)) &&
            !DisplayTopologyService.IsClearPhysicalModeDrift(
                new AdvertisedDisplayMode(800, 600, 60),
                new AdvertisedDisplayMode(800, 600, 60)),
            "Physical-mode repair would override an intentional temporary mode.");
        Require(StreamingHostLocator.ExtractExecutablePath(
                @"""C:\Program Files\Sunshine\sunshinesvc.exe"" --service") ==
                @"C:\Program Files\Sunshine\sunshinesvc.exe",
            "Quoted Sunshine service path parsing failed.");
        Require(StreamingHostLocator.ExtractExecutablePath(
                @"C:\Sunshine Portable\sunshine.exe --service") ==
                @"C:\Sunshine Portable\sunshine.exe",
            "Unquoted Sunshine service path parsing failed.");
        var unsupportedTaskAccountMessage =
            ScheduledTaskAccount.BuildUnsupportedAccountMessageForTest(
                "Vita Moonlight setup or repair",
                @"DESKTOP-TEST\Administrator",
                @"DESKTOP-TEST\Streamer");
        Require(
            ScheduledTaskAccount.AccountsMatchForTest(
                @"DESKTOP-TEST\Streamer",
                @"desktop-test\streamer") &&
            !ScheduledTaskAccount.AccountsMatchForTest(
                @"DESKTOP-TEST\Administrator",
                @"DESKTOP-TEST\Streamer") &&
            unsupportedTaskAccountMessage.Contains(
                @"DESKTOP-TEST\Administrator",
                StringComparison.Ordinal) &&
            unsupportedTaskAccountMessage.Contains(
                @"DESKTOP-TEST\Streamer",
                StringComparison.Ordinal) &&
            unsupportedTaskAccountMessage.Contains(
                "different administrator password is intentionally rejected",
                StringComparison.Ordinal),
            "Interactive scheduled-task account classification failed.");
        var supportDiagnostics = new HostDiagnosticReport(
            IsWindows: true,
            OperatingSystem: "Test Windows",
            Architecture: "x64; Client",
            IsSupportedPlatform: true,
            IsAdministrator: false,
            ScheduledTaskAccountReady: true,
            ScheduledTaskAccountMessage: "Recovery tasks target the streaming account.",
            BackendStatus: BackendLifecycleStatus.Enabled,
            BackendDesiredState: BackendDesiredState.Enabled,
            BackendPreferencePersisted: true,
            BackendIssues: Array.Empty<string>(),
            BackendIssueCodes: Array.Empty<string>(),
            BackendActivePhysicalDisplayCount: 1,
            BackendManagedVddDeviceCount: 1,
            BackendManagedVddEnabledCount: 1,
            BackendManagedVddActive: false,
            HostMode: @"C:\Users\PrivateName\malformed-host-mode",
            SunshinePath: @"C:\Users\PrivateName\Sunshine\sunshine.exe",
            SunshineVersion: "1.2.3",
            SunshineVersionSupported: true,
            ApolloPath: null,
            ViGEmBusInstalled: true,
            ViGEmBusRunning: true,
            SunshineNeedsRestart: false,
            DisplayWizardPath: @"C:\Private\Bundle\DisplayWizard.exe",
            VisualCppRuntimeVersion: "14.44",
            VisualCppRuntimeSupported: true,
            VirtualDisplayDriverInstalled: true,
            VirtualDisplayModesReady: true,
            RecoveryPending: false,
            RecoveryTaskInstalled: true,
            RecoveryTaskState: ExactScheduledTaskState.Present,
            RescueAgentInstalled: true,
            RescueAgentTaskState: ExactScheduledTaskState.Present,
            RescueAgentRunning: true,
            ModeHotkeys: Array.Empty<HostModeHotkeyStatus>(),
            IntegrateAllSunshineApps: true,
            ForceSdr: true,
            NativeDisplayLifecycleReady: true,
            Recommendation: "Ready");
        var supportReport = SupportReportExporter.Create(
            supportDiagnostics,
            new[]
            {
                new SupportDisplayReport(
                    1,
                    @"\\?\DISPLAY#PrivateName#1",
                    true,
                    true,
                    true,
                    false,
                    WindowsDisplayNative.OutputTechnologyIndirectWired,
                    960,
                    544,
                    60),
            },
            null,
            installedPackage: true,
            hostVersion: "0.0-test");
        var supportJson = SupportReportExporter.Serialize(supportReport);
        Require(
            supportReport.SchemaVersion == SupportReportExporter.CurrentSchemaVersion &&
            supportReport.ScheduledTaskAccountReady &&
            supportReport.SunshineInstalled &&
            supportReport.BackendStatus == "Enabled" &&
            supportReport.RecoveryTaskStatus == "Present" &&
            supportReport.HostMode == "unknown" &&
            supportReport.Displays.Count == 1 &&
            supportReport.Displays[0].Name == "Vita virtual display" &&
            supportReport.Displays[0].Width == 960 &&
            supportReport.BackendActivePhysicalDisplayCount == 1,
            "Support report schema or component projection failed.");
        Require(
            !supportJson.Contains("PrivateName", StringComparison.Ordinal) &&
            !supportJson.Contains(@"C:\Private", StringComparison.Ordinal) &&
            !supportJson.Contains("sunshine.exe", StringComparison.OrdinalIgnoreCase),
            "Support report exposed a private component path.");

        Console.WriteLine("Host companion self-test passed.");
        return ExitSuccess;
    }

    private static void RunOwnedStateCleanupSelfTest()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            $"vita-moonlight-uninstall-self-test-{Guid.NewGuid():N}");
        var currentRoot = Path.Combine(testRoot, "current");
        var legacyRoot = Path.Combine(testRoot, "legacy");
        var diagnosticsRoot = Path.Combine(currentRoot, "Diagnostics");
        var unknownSentinel = Path.Combine(currentRoot, "community-notes.txt");
        var uninstallGuard = Path.Combine(
            currentRoot,
            "uninstall-in-progress.intent");
        var lockedState = Path.Combine(currentRoot, "session.lock");
        try
        {
            Directory.CreateDirectory(diagnosticsRoot);
            Directory.CreateDirectory(legacyRoot);
            File.WriteAllText(
                Path.Combine(currentRoot, "display-recovery.json"),
                "owned");
            File.WriteAllText(
                Path.Combine(currentRoot, "backend-disabled.intent"),
                "owned");
            File.WriteAllText(
                Path.Combine(diagnosticsRoot, "stream-rescue.log"),
                "owned");
            File.WriteAllText(
                Path.Combine(legacyRoot, "host-settings.json"),
                "owned");
            File.WriteAllText(unknownSentinel, "retain");
            File.WriteAllText(
                uninstallGuard,
                "vita-moonlight-uninstall-in-progress-v1");
            File.WriteAllText(lockedState, "owned but temporarily busy");

            using (var busyState = new FileStream(
                       lockedState,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                var first = UninstallManager.CleanupOwnedStateFiles(
                    currentRoot,
                    legacyRoot);
                Require(
                    first.RemovedFiles == 4 &&
                    first.RemovedDirectories == 2 &&
                    first.RetainedEntries.Any(path =>
                        string.Equals(
                            Path.GetFullPath(path),
                            Path.GetFullPath(currentRoot),
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal)) &&
                    File.Exists(unknownSentinel) &&
                    File.Exists(uninstallGuard) &&
                    File.Exists(lockedState) &&
                    !File.Exists(Path.Combine(
                        currentRoot,
                        "display-recovery.json")) &&
                    !File.Exists(Path.Combine(
                        currentRoot,
                        "backend-disabled.intent")) &&
                    !Directory.Exists(diagnosticsRoot) &&
                    !Directory.Exists(legacyRoot),
                    "Uninstall cleanup stopped after a busy owned file, removed unknown state, or removed its guard.");
            }

            var second = UninstallManager.CleanupOwnedStateFiles(
                currentRoot,
                legacyRoot);
            Require(
                second.RemovedFiles == 1 &&
                second.RemovedDirectories == 0 &&
                File.Exists(unknownSentinel) &&
                File.Exists(uninstallGuard) &&
                !File.Exists(lockedState),
                "Uninstall cleanup was not idempotent or removed an unknown/transaction-guard file on retry.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    private static int PrintHelp()
    {
        Console.WriteLine("VitaMoonlight.Host gui");
        Console.WriteLine("VitaMoonlight.Host doctor [--json]");
        Console.WriteLine("VitaMoonlight.Host profile [--json]");
        Console.WriteLine("VitaMoonlight.Host configure [--host sunshine] [--config-dir PATH] [--display-match TEXT] [--all-apps true|false] [--force-sdr true|false]");
        Console.WriteLine("  Experimental only: configure --host apollo requires an explicit --display-match and is not public-beta qualified.");
        Console.WriteLine("VitaMoonlight.Host host status|restart [--host sunshine]");
        Console.WriteLine("VitaMoonlight.Host host ensure-compatible --installer PATH");
        Console.WriteLine("VitaMoonlight.Host gamepad status|ensure-compatible [--installer PATH]");
        Console.WriteLine("VitaMoonlight.Host runtime status|ensure-compatible [--installer PATH]");
        Console.WriteLine("VitaMoonlight.Host dependency uninstall sunshine|vigembus");
        Console.WriteLine("VitaMoonlight.Host state secure");
        Console.WriteLine("VitaMoonlight.Host maintenance begin|end|backend-was-enabled|rescue-task-was-present|recovery-task-was-present|vdd-adoption-required --owner-pid PID|status");
        Console.WriteLine("VitaMoonlight.Host deferred-setup save --host sunshine [--virtual-driver true] [--adoption-owner-pid PID]|clear|status");
        Console.WriteLine("VitaMoonlight.Host backend enable|disable|status [--json] [--require-enabled] [--intent-exit-code]");
        Console.WriteLine("VitaMoonlight.Host driver install [--adopt-existing-vdd-id INSTANCE_ID]|adopt-idle --adoption-owner-pid PID|reload|uninstall|status");
        Console.WriteLine("VitaMoonlight.Host display list");
        Console.WriteLine("VitaMoonlight.Host display disable-virtual");
        Console.WriteLine("VitaMoonlight.Host session test --width 960|1280 --height 540|544|720 --fps 24|30|40|50|60 [--seconds 5..120]");
        Console.WriteLine("VitaMoonlight.Host session start --width 960|1280 --height 540|544|720 --fps 24|30|40|50|60");
        Console.WriteLine("VitaMoonlight.Host session hook-start --width N --height N --fps N (Sunshine prep hook)");
        Console.WriteLine("VitaMoonlight.Host session mode --width 960|1280 --height 540|544|720 [--fps 60]");
        Console.WriteLine("VitaMoonlight.Host session stop|recover|recover-upgrade|status");
        Console.WriteLine("VitaMoonlight.Host recovery install|uninstall|status|task-status");
        Console.WriteLine("VitaMoonlight.Host agent run [--background]|install|uninstall|status|task-status");
        Console.WriteLine("VitaMoonlight.Host emergency recover-display|reset-display-driver");
        Console.WriteLine("VitaMoonlight.Host support export [--output PATH]");
        Console.WriteLine("VitaMoonlight.Host uninstall prepare|cleanup-integration|finalize-owned [--sunshine-removed] [--vdd-removed]");
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

    private static string? GetExpectedVddAdoptionInstanceId(
        string[] args)
    {
        if (HasFlag(args, "--adopt-existing-vdd"))
        {
            throw new ArgumentException(
                "--adopt-existing-vdd no longer grants unbound device adoption. Use the control-panel question, or supply the exact --adopt-existing-vdd-id shown by `display list`.");
        }
        var direct = GetOption(args, "--adopt-existing-vdd-id");
        var ownerText = GetOption(args, "--adoption-owner-pid");
        if (direct is not null && ownerText is not null)
        {
            throw new ArgumentException(
                "Specify either --adopt-existing-vdd-id or --adoption-owner-pid, not both.");
        }

        string? candidate = direct;
        if (ownerText is not null)
        {
            if (!int.TryParse(ownerText, out var ownerProcessId) ||
                ownerProcessId <= 0)
            {
                throw new ArgumentException(
                    "--adoption-owner-pid requires a positive process id.");
            }
            candidate = InstallerMaintenanceFence
                .RequireStagedVddAdoptionCandidateForOwner(
                    ownerProcessId);
        }
        if (candidate is not null &&
            !ManagedVddOwnershipJournal.IsValidInstanceIdForAdoption(
                candidate))
        {
            throw new ArgumentException(
                "The expected existing virtual-display instance ID is invalid.");
        }
        return candidate;
    }

    private static bool TryDescribeUnrelatedSunshineHook(
        string command,
        string[] args,
        out string message)
    {
        message = string.Empty;
        if (!command.Equals("session", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                args.FirstOrDefault(),
                "hook-start",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int width;
        int height;
        int streamFps;
        try
        {
            width = GetRequiredInt(args, "--width");
            height = GetRequiredInt(args, "--height");
            streamFps = GetRequiredInt(args, "--fps");
        }
        catch (ArgumentException)
        {
            // A malformed generated command is a packaging/configuration bug,
            // not an unrelated client. Let normal validation report it.
            return false;
        }

        if (VitaDisplayModes.TryGetSupportedStreamMode(
                width,
                height,
                streamFps,
                out _))
        {
            return false;
        }

        message =
            $"Sunshine client mode {width}x{height} at {streamFps} FPS " +
            "is not a Vita mode; the host display was left unchanged.";
        return true;
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

    private static void TryWriteMaintenanceHelperError(string details)
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) ||
                !Path.GetFileName(executable).Equals(
                    "VitaMoonlight.Host.Maintenance.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            var helperDirectory = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
            var relative = Path.GetRelativePath(
                temporaryRoot,
                helperDirectory);
            if (relative == ".." ||
                relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                return;
            }
            File.WriteAllText(
                Path.Combine(
                    helperDirectory,
                    "VitaMoonlight.Host.Maintenance.error.txt"),
                details + Environment.NewLine);
        }
        catch
        {
            // The process exit code remains authoritative when even the
            // setup-private diagnostic channel is unavailable.
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

    private static void EnsureMaintenanceAdministrator(string operation)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Administrator detection is only supported on Windows.");
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(
                WindowsBuiltInRole.Administrator))
        {
            throw new UnauthorizedAccessException(
                $"{operation} requires an Administrator terminal.");
        }
        InstallerMaintenanceFence.RequireControllerProcess();
    }

    private static void EnsureBackendEnabled(string message)
    {
        if (!BackendLifecycleManager.IsEnabled)
        {
            throw new InvalidOperationException(message + ".");
        }
    }

    private static void EnsureBackendReadyForStreaming()
    {
        var report = BackendLifecycleManager.Inspect();
        if (report.Status != BackendLifecycleStatus.Enabled ||
            report.Components is null ||
            !report.Components.RecoveryTaskInstalled ||
            !report.Components.RescueAgentTaskInstalled ||
            !report.Components.RescueAgentRunning)
        {
            throw new InvalidOperationException(
                "Vita display switching is paused or its recovery safeguards are not fully ready. " +
                "Open the host control panel as Administrator, choose Enable Vita host features, " +
                "then run Check readiness before starting a Vita session");
        }
        if (!IsVirtualDisplayReady(out var displayReadiness))
        {
            throw new InvalidOperationException(
                displayReadiness +
                " Open Display & recovery and repair the Vita display driver before streaming");
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
        Converters =
        {
            new System.Text.Json.Serialization.JsonStringEnumConverter(),
        },
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
    bool ScheduledTaskAccountReady,
    string ScheduledTaskAccountMessage,
    BackendLifecycleStatus BackendStatus,
    BackendDesiredState BackendDesiredState,
    bool BackendPreferencePersisted,
    IReadOnlyList<string> BackendIssues,
    IReadOnlyList<string> BackendIssueCodes,
    int BackendActivePhysicalDisplayCount,
    int BackendManagedVddDeviceCount,
    int BackendManagedVddEnabledCount,
    bool BackendManagedVddActive,
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
    ExactScheduledTaskState RecoveryTaskState,
    bool RescueAgentInstalled,
    ExactScheduledTaskState RescueAgentTaskState,
    bool RescueAgentRunning,
    IReadOnlyList<HostModeHotkeyStatus> ModeHotkeys,
    bool IntegrateAllSunshineApps,
    bool ForceSdr,
    bool NativeDisplayLifecycleReady,
    string Recommendation)
{
    public bool HasStreamingHost => SunshinePath is not null || ApolloPath is not null;
    public bool HasSelectedStreamingHost =>
        HostMode == "sunshine" && SunshinePath is not null;
    public bool HasDisplaySupport =>
        HostMode == "sunshine" &&
        DisplayWizardPath is not null &&
        VirtualDisplayDriverInstalled &&
        VirtualDisplayModesReady;
    public bool ModeHotkeysReady =>
        HostRecoveryAgentManager.LegacyModeHotkeysRequired(
            HostSettings.Default with
            {
                HostMode = HostMode,
                IntegrateAllSunshineApps = IntegrateAllSunshineApps,
            })
            ? ModeHotkeys.Count == VitaDisplayModes.Supported.Count &&
              ModeHotkeys.All(status => status.Ready)
            : ModeHotkeys.Count == 0;
}

internal static class HostDiagnostics
{
    public static HostDiagnosticReport Inspect()
    {
        var settings = HostSettings.Load();
        var backend = BackendLifecycleManager.Inspect();
        var platform = WindowsPlatformCompatibility.Inspect();
        var isWindows = platform.IsWindows;
        var architecture = platform.IsWindows
            ? $"{platform.ArchitectureDescription}; {platform.InstallationType ?? "unknown Windows type"}"
            : platform.ArchitectureDescription;
        var supportedPlatform = platform.IsSupported;
        var scheduledTaskAccount = ScheduledTaskAccount.Inspect();
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
        var nativeVerificationCurrent =
            DriverNativeModeVerification.TryGetCurrent(
                out var nativeVerification,
                out _);
        var virtualDisplayModesReady =
            DisplayWizardAdapter.HasVitaCompatibilityModes() &&
            nativeVerificationCurrent;
        var recoveryPending = File.Exists(HostStatePaths.RecoveryFile);
        var recoveryTask = RecoveryTaskManager.GetInstallationState();
        var recoveryTaskInstalled = recoveryTask.State ==
            ExactScheduledTaskState.Present;
        var rescueAgentTask = HostRecoveryAgentManager.GetInstallationState();
        var rescueAgentInstalled = rescueAgentTask.State ==
            ExactScheduledTaskState.Present;
        var rescueAgentRunning = HostRecoveryAgentManager.IsRunning();
        var modeHotkeys = HostRecoveryAgentManager.GetModeHotkeyReadiness();
        var legacyModeHotkeysRequired =
            HostRecoveryAgentManager.LegacyModeHotkeysRequired(settings);
        var modeHotkeysReady = legacyModeHotkeysRequired
            ? modeHotkeys.Count == VitaDisplayModes.Supported.Count &&
              modeHotkeys.All(status => status.Ready)
            : modeHotkeys.Count == 0;
        var hostConfigurationDirectory =
            SunshineConfigurator.ResolveConfigurationDirectory(
                settings.SunshineConfigDirectory,
                settings.HostMode);
        var companionPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "VitaMoonlight.Host.exe"));
        var automaticDisplayLifecycleReady =
            settings.HostMode == "sunshine" &&
            settings.IntegrateAllSunshineApps
                ? nativeVerificationCurrent &&
                  SunshineConfigurator.IsAuthenticatedStreamBoundaryConfigurationReady(
                    hostConfigurationDirectory,
                    settings.ForceSdr,
                    settings.DisplayMatch,
                    nativeVerification?.VerifiedAt)
                : SunshineConfigurator.IsManagedHookReady(
                    hostConfigurationDirectory,
                    settings.SunshineApplicationName,
                    companionPath);

        var recommendation = !isWindows
            ? "Run this companion on a Windows 10 version 2004 or newer / Windows 11 x64 streaming host."
            : !supportedPlatform
                ? platform.RequirementMessage
            : !scheduledTaskAccount.IsSameInteractiveAccount
                ? scheduledTaskAccount.Message +
                  " Sign in to the administrator account used for streaming and run setup there; do not approve setup with a different account's credentials."
            : recoveryPending
                ? "An earlier session did not restore its display layout. Open Display & recovery and choose Restore physical display now before streaming."
                : backend.Status == BackendLifecycleStatus.Error
                    ? "The protected Vita host-feature state could not be verified. Open the control panel as Administrator and review Diagnostics & support before enabling or pausing Vita host features."
                : backend.DesiredState == BackendDesiredState.Disabled &&
                  backend.Status == BackendLifecycleStatus.Partial
                    ? "Vita host features are set to paused, but one or more owned components could not be stopped safely. Open Overview as Administrator and choose Pause Vita host features again."
                : backend.Status == BackendLifecycleStatus.Disabled
                    ? "Vita host features are paused by you. Shared streaming servers remain installed and reachable, but clients pinned to the Vita virtual display may need a physical output; choose Enable Vita host features on Overview before the next Vita session."
                : settings.HostMode == "apollo"
                    ? "Apollo is an experimental CLI-only compatibility path and is not public-beta qualified. Open Streaming, select the supported Sunshine configuration, then run Set up or repair this PC. Apollo itself and unrelated settings are preserved."
                    : settings.HostMode == "sunshine" && sunshine is null
                        ? "Install Sunshine with Set up or repair this PC, then run this check again."
                        : settings.HostMode == "sunshine" && !sunshineCompatibility.IsSupported
                            ? $"Update Sunshine to {SunshineCompatibility.MinimumVersionText} or newer with the Vita Moonlight host installer."
                    : settings.HostMode == "sunshine" && !visualCppRuntime.IsSupported
                        ? $"Microsoft Visual C++ runtime {VisualCppRuntimeCompatibility.MinimumVersionText} or newer is required. Open Get started and choose Set up or repair this PC."
                    : !vigem
                        ? "Controller support is missing. Open Get started and choose Set up or repair this PC, then restart Windows if requested."
                        : !vigemRunning
                                ? "Controller support is installed but is not running. Restart Windows; if it remains stopped, open Diagnostics & support and choose Repair controller support."
                            : sunshineNeedsRestart
                                    ? "Controller support is ready, but Sunshine started before it. Open Get started and choose Set up or repair this PC to restart Sunshine safely."
                        : !automaticDisplayLifecycleReady
                            ? "The selected streaming host does not have a current, verified Vita display lifecycle configuration. Open Get started and choose Set up or repair this PC."
                        : settings.HostMode == "sunshine" && displayWizard is null
                            ? "Reinstall the host companion so its signed display-driver bundle is available."
                            : settings.HostMode == "sunshine" && !virtualDisplay
                                ? "Install the signed virtual display driver, reboot if requested, and run this check again."
                                : settings.HostMode == "sunshine" && !virtualDisplayModesReady
                                    ? "The Vita display modes are missing or native 960x544 is not verified. Open Display & recovery, repair the Vita display driver, then run Set up or repair this PC."
                                : recoveryTask.State == ExactScheduledTaskState.Unknown
                                    ? "Windows could not inspect the automatic recovery task. Confirm the Task Scheduler service is running, then run this check again."
                                : !recoveryTaskInstalled
                                    ? "Automatic sign-in recovery is not installed. Open the control panel as Administrator, then run Set up or repair this PC."
                                : rescueAgentTask.State == ExactScheduledTaskState.Unknown
                                    ? "Windows could not inspect the stream-rescue task. Confirm the Task Scheduler service is running, then run this check again."
                                : !rescueAgentInstalled || !rescueAgentRunning
                                    ? "Authenticated Vita handoff and stream recovery are not ready. Open Get started and choose Set up or repair this PC."
                                : !modeHotkeysReady
                                    ? "One or more display-mode shortcuts are unavailable. Close software using Ctrl+Alt+Shift+F8/F9/F10, then open Diagnostics & support and repair stream rescue shortcuts."
                                : backend.Status == BackendLifecycleStatus.Partial
                                    ? "The enabled Vita host features are only partly applied. Open Overview as Administrator and choose Enable Vita host features again."
                                : "This PC is ready. Install the matching Vita VPK, pair with Sunshine, and launch Steam Big Picture, Desktop, or a game.";

        return new HostDiagnosticReport(
            isWindows,
            Environment.OSVersion.VersionString,
            architecture,
            supportedPlatform,
            IsAdministrator(),
            scheduledTaskAccount.IsSameInteractiveAccount,
            scheduledTaskAccount.Message,
            backend.Status,
            backend.DesiredState,
            backend.PreferencePersisted,
            backend.Issues,
            backend.Issues.Select(BackendIssueCode).Distinct().ToArray(),
            backend.Components?.ActivePhysicalDisplayCount ?? 0,
            backend.Components?.ManagedVirtualDisplayDevices.Count ?? 0,
            backend.Components?.ManagedVirtualDisplayDevices.Count(device =>
                device.Present && device.Enabled) ?? 0,
            backend.Components?.ManagedVirtualDisplayActive ?? false,
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
            recoveryTask.State,
            rescueAgentInstalled,
            rescueAgentTask.State,
            rescueAgentRunning,
            modeHotkeys,
            settings.IntegrateAllSunshineApps,
            settings.ForceSdr,
            automaticDisplayLifecycleReady,
            recommendation);
    }

    private static string BackendIssueCode(string issue)
    {
        if (issue.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
            issue.Contains("progress", StringComparison.OrdinalIgnoreCase))
        {
            return "uninstall-transaction";
        }
        if (issue.Contains("physical display", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("physical-display", StringComparison.OrdinalIgnoreCase))
        {
            return "physical-display";
        }
        if (issue.Contains("recovery task", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("Task Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            return "recovery-task";
        }
        if (issue.Contains("rescue", StringComparison.OrdinalIgnoreCase))
        {
            return "rescue-agent";
        }
        if (issue.Contains("virtual display", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("VDD", StringComparison.OrdinalIgnoreCase))
        {
            return "managed-vdd";
        }
        if (issue.Contains("lifecycle", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("protected backend", StringComparison.OrdinalIgnoreCase) ||
            issue.Contains("host-feature state", StringComparison.OrdinalIgnoreCase))
        {
            return "lifecycle-state";
        }
        return "backend-other";
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
