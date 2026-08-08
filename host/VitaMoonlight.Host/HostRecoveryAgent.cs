using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record HostRescueStatus(
    DateTimeOffset Timestamp,
    string Action,
    bool Success,
    string Message);

internal sealed record HostModeHotkeyStatus(
    VitaDisplayMode Mode,
    bool Ready);

internal static class HostRecoveryAgentManager
{
    internal const string TaskName = "Vita Moonlight stream rescue agent";
    internal const string MutexName = @"Local\VitaMoonlight.StreamRescueAgent";
    internal const string ReadyEventName = @"Local\VitaMoonlight.StreamRescueAgent.Ready";
    internal const string WindowCaption = "Vita Moonlight stream rescue agent";
    private const int WmClose = 0x0010;

    internal static int Run(bool hideConsole)
    {
        if (hideConsole)
        {
            var console = GetConsoleWindow();
            if (console != IntPtr.Zero) ShowWindow(console, 0);
        }
        using var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(0, false)) return 0;
        }
        catch (AbandonedMutexException)
        {
            // The previous agent stopped unexpectedly; this process owns the mutex now.
        }

        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEventName);
        ready.Reset();
        try
        {
            HostRecoveryActions.RecoverStaleSuspendIntentAtAgentStartup();
            using var context = new HostRecoveryAgentContext();
            ready.Set();
            Application.Run(context);
            return context.StreamBoundaryListenerFaulted ? 1 : 0;
        }
        finally
        {
            ready.Reset();
            mutex.ReleaseMutex();
        }
    }

    internal static bool IsInstalled() =>
        GetInstallationState().State ==
        ExactScheduledTaskState.Present;

    internal static ExactScheduledTaskProbe GetInstallationState() =>
        ExactScheduledTaskManager.Probe(TaskName);

    internal static bool IsRunning()
    {
        if (!IsProcessPresent() || !IsEventSignaled(ReadyEventName))
        {
            return false;
        }
        try
        {
            var bridge = SunshineStreamBridgeConfiguration.LoadIfEnabled();
            return bridge is null || ManagedStreamBridgeFirewall.IsReady(
                bridge.Port,
                Environment.ProcessPath ?? Path.Combine(
                    AppContext.BaseDirectory,
                    "VitaMoonlight.Host.exe"));
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<HostModeHotkeyStatus> GetModeHotkeyReadiness()
    {
        if (!LegacyModeHotkeysRequired(HostSettings.Load()))
        {
            return Array.Empty<HostModeHotkeyStatus>();
        }
        var agentReady = IsRunning();
        return VitaDisplayModes.Supported
            .Select(mode => new HostModeHotkeyStatus(
                mode,
                agentReady && IsEventSignaled(ModeHotkeyReadyEventName(mode))))
            .ToArray();
    }

    internal static bool LegacyModeHotkeysRequired(HostSettings settings) =>
        !string.Equals(settings.HostMode, "sunshine", StringComparison.OrdinalIgnoreCase) ||
        !settings.IntegrateAllSunshineApps;

    internal static string ModeHotkeyReadyEventName(VitaDisplayMode mode) =>
        $@"Local\VitaMoonlight.StreamRescueAgent.Mode.{mode.Width}x{mode.Height}x{mode.Fps}";

    private static bool IsProcessPresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var mutex = Mutex.OpenExisting(MutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsEventSignaled(string name)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(name);
            return ready.WaitOne(0);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static void Install(string executablePath)
    {
        ScheduledTaskAccount.RequireCurrentInteractiveUser(
            "Installing the stream rescue agent");
        var existing = GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(existing, TaskName);
        if (existing.State == ExactScheduledTaskState.Present)
        {
            ExactScheduledTaskManager.RequireOwnedInteractiveTask(
                TaskName,
                executablePath,
                "agent run --background",
                requireInteractiveHighest: false);
            ExactScheduledTaskManager.StopExact(TaskName);
        }
        StopCurrentSessionAgent();
        var taskCommand = $"\"{Path.GetFullPath(executablePath)}\" agent run --background";
        var createExitCode = RunTask(
            "/Create", "/F",
            "/TN", TaskName,
            "/TR", taskCommand,
            "/SC", "ONLOGON",
            "/IT",
            "/RL", "HIGHEST");
        if (createExitCode != 0)
        {
            throw new InvalidOperationException("Windows could not create the stream rescue agent task.");
        }
        ConfigurePersistentTask();
        ExactScheduledTaskManager.RequireOwnedInteractiveTask(
            TaskName,
            executablePath,
            "agent run --background");
        var bridge = SunshineStreamBridgeConfiguration.LoadIfEnabled();
        if (bridge is null)
        {
            ManagedStreamBridgeFirewall.RemoveOwned(executablePath);
        }
        else
        {
            ManagedStreamBridgeFirewall.InstallOrRepair(
                bridge.Port,
                executablePath);
        }
        if (RunTask("/Run", "/TN", TaskName) != 0)
        {
            throw new InvalidOperationException("Windows created the stream rescue agent but could not start it.");
        }
        for (var attempt = 0; attempt < 100 && !IsRunning(); attempt++) Thread.Sleep(100);
        if (!IsRunning())
        {
            throw new InvalidOperationException(
                "Windows started the stream rescue task, but authenticated stream handoff and display recovery did not become ready.");
        }
    }

    internal static void Uninstall()
    {
        var existing = GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(existing, TaskName);
        if (existing.State == ExactScheduledTaskState.Present)
        {
            ExactScheduledTaskManager.RequireOwnedInteractiveTask(
                TaskName,
                Environment.ProcessPath ?? Path.Combine(
                    AppContext.BaseDirectory,
                    "VitaMoonlight.Host.exe"),
                "agent run --background",
                requireInteractiveHighest: false);
            ExactScheduledTaskManager.StopExact(TaskName);
        }
        StopCurrentSessionAgent();
        ExactScheduledTaskManager.DeleteExact(TaskName);
        ManagedStreamBridgeFirewall.RemoveOwned(
            Environment.ProcessPath ?? Path.Combine(
                AppContext.BaseDirectory,
                "VitaMoonlight.Host.exe"));
    }

    internal static HostRescueStatus? ReadLastStatus()
    {
        try
        {
            return File.Exists(HostStatePaths.RescueStatusFile)
                ? JsonSerializer.Deserialize<HostRescueStatus>(
                    TrustedFileSystem.ReadAllText(
                        HostStatePaths.RescueStatusFile))
                : null;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception or
                JsonException)
        {
            return null;
        }
    }

    private static int RunTask(params string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(true);
                throw new TimeoutException("Windows Task Scheduler did not respond.");
            }
            Task.WaitAll(output, error);
            return process.ExitCode;
        }
        catch
        {
            return 1;
        }
    }

    private static void StopCurrentSessionAgent()
    {
        var window = FindWindow(null, WindowCaption);
        uint processId = 0;
        if (window != IntPtr.Zero) GetWindowThreadProcessId(window, out processId);
        if (window != IntPtr.Zero) PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        for (var attempt = 0; attempt < 10 && IsProcessPresent(); attempt++) Thread.Sleep(100);
        if (IsProcessPresent() && processId > 4 && processId != Environment.ProcessId)
        {
            try
            {
                using var process = Process.GetProcessById(checked((int)processId));
                if (process.ProcessName.Equals("VitaMoonlight.Host", StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The final mutex check below reports a persistent agent with actionable guidance.
            }
        }
        for (var attempt = 0; attempt < 20 && IsProcessPresent(); attempt++) Thread.Sleep(100);
        if (IsProcessPresent())
        {
            throw new InvalidOperationException(
                "An earlier stream rescue agent is still running. Sign out once, then repair the agent from the control panel.");
        }
    }

    private static void ConfigurePersistentTask()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script =
            $"$task = Get-ScheduledTask -TaskName '{TaskName}' -TaskPath '\\'; " +
            "$task.Settings.ExecutionTimeLimit = 'PT0S'; " +
            "$task.Settings.DisallowStartIfOnBatteries = $false; " +
            "$task.Settings.StopIfGoingOnBatteries = $false; " +
            "$task.Settings.StartWhenAvailable = $true; " +
            "$task.Settings.RestartCount = 5; " +
            "$task.Settings.RestartInterval = 'PT1M'; " +
            "$task.Settings.MultipleInstances = 'IgnoreNew'; " +
            "Set-ScheduledTask -InputObject $task | Out-Null";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = powerShell,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(script);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000))
        {
            process.Kill(true);
            throw new TimeoutException("Windows did not finish configuring the stream rescue task.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows could not configure the stream rescue agent for continuous operation.");
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

internal sealed class HostRecoveryAgentContext : ApplicationContext
{
    private readonly HostRecoveryHotkeyWindow window;
    private readonly StreamBoundaryBridgeServer? streamBoundaryBridge;
    private int streamBoundaryListenerFaulted;

    internal bool StreamBoundaryListenerFaulted =>
        Volatile.Read(ref streamBoundaryListenerFaulted) != 0;

    internal HostRecoveryAgentContext()
    {
        window = new HostRecoveryHotkeyWindow();
        try
        {
            streamBoundaryBridge =
                StreamBoundaryBridgeServer.CreateIfEnabled(() =>
                {
                    Interlocked.Exchange(
                        ref streamBoundaryListenerFaulted,
                        1);
                    Application.Exit();
                });
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            streamBoundaryBridge?.Dispose();
            window.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal enum ResumeTopologyDecision
{
    Wait,
    Healthy,
    ReconcileIdle,
    Recover,
}

internal static class ResumeTopologyClassifier
{
    internal const int HealthySamplesRequired = 2;
    internal const int RecoverySamplesRequired = 3;

    internal static ResumeTopologyDecision Classify(
        int activePhysicalPaths,
        int activeManagedVirtualPaths,
        string? pendingTransactionAtWake,
        string? currentPendingTransaction,
        int stableSamples,
        bool minimumRecoveryAgeReached,
        bool managedVirtualPnpDisabled = true)
    {
        if (activePhysicalPaths < 0 ||
            activeManagedVirtualPaths < 0 ||
            stableSamples < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(activePhysicalPaths),
                "Display path and sample counts cannot be negative.");
        }

        // A marker that appeared (or was replaced) after the resume event is
        // a new live session. Never tear it down. A marker already present at
        // resume belongs to the interrupted pre-sleep session and must be
        // recovered after the topology has settled.
        if (SessionStartedAfterResume(
                pendingTransactionAtWake,
                currentPendingTransaction))
        {
            return ResumeTopologyDecision.Healthy;
        }

        var interruptedSessionPending =
            pendingTransactionAtWake is not null &&
            string.Equals(
                pendingTransactionAtWake,
                currentPendingTransaction,
                StringComparison.Ordinal);
        var physicalOnlyIsHealthy =
            activePhysicalPaths > 0 &&
            activeManagedVirtualPaths == 0 &&
            !interruptedSessionPending;
        if (physicalOnlyIsHealthy)
        {
            if (stableSamples < HealthySamplesRequired)
            {
                return ResumeTopologyDecision.Wait;
            }
            return managedVirtualPnpDisabled
                ? ResumeTopologyDecision.Healthy
                : ResumeTopologyDecision.ReconcileIdle;
        }

        return stableSamples >= RecoverySamplesRequired &&
               minimumRecoveryAgeReached
            ? ResumeTopologyDecision.Recover
            : ResumeTopologyDecision.Wait;
    }

    internal static bool SessionStartedAfterResume(
        string? pendingTransactionAtWake,
        string? currentPendingTransaction) =>
        currentPendingTransaction is not null &&
        (pendingTransactionAtWake is null ||
         !string.Equals(
             pendingTransactionAtWake,
             currentPendingTransaction,
             StringComparison.Ordinal));
}

internal sealed class HostRecoveryHotkeyWindow : NativeWindow, IDisposable
{
    private const int WmClose = 0x0010;
    private const int WmDisplayChange = 0x007E;
    private const int WmDeviceChange = 0x0219;
    private const int WmPowerBroadcast = 0x0218;
    private const int WmHotkey = 0x0312;
    private const int PbtApmSuspend = 0x0004;
    private const int PbtApmResumeAutomatic = 0x0012;
    private const int RecoverDisplayHotkeyId = 2;
    private const int Mode960x540HotkeyId = 3;
    private const int Mode960x544HotkeyId = 4;
    private const int Mode1280x720HotkeyId = 5;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF11 = 0x7A;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private static readonly TimeSpan ResumeObservationWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MaximumResumeRecoveryRuntime =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResumeInitialDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResumeFollowupDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ResumeSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumRecoveryAge = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SuspendDisplayLeaseBudget =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SunshineAbsentPollInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SunshineDisconnectGrace =
        TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan SunshineExitRecoveryBudget =
        TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SunshineRecoveryRetryInterval =
        TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SunshineWatcherDisposeWait =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingAudioRecoveryInitialDelay =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PendingAudioRecoveryRetryInterval =
        TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PendingAudioRecoveryBudget =
        TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingAudioRecoveryDeviceEventCooldown =
        TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingAudioRecoveryDisposeWait =
        TimeSpan.FromSeconds(2);
    private const int MaximumPendingAudioRecoveryAttempts = 30;
    private const int MaximumSunshineConfigurationBytes = 1024 * 1024;
    private const int MaximumSunshineLogTailBytes = 1024 * 1024;
    private const int ResumeSampleAttempts = 12;
    private const int SuspendActionWaitMilliseconds = 500;
    private const int DisplayActionRunning = 1;
    private readonly HashSet<int> registeredHotkeys = new();
    private readonly List<EventWaitHandle> modeReadinessEvents = new();
    private readonly object resumeInspectionSync = new();
    private readonly CancellationTokenSource sunshineWatchCancellation = new();
    private readonly object sunshineLifecycleSync = new();
    private readonly CancellationTokenSource pendingAudioRecoveryCancellation =
        new();
    private readonly object pendingAudioRecoverySync = new();
    private readonly bool sunshineLifecycleEnabled;
    private readonly bool legacySunshineLogObserverEnabled;
    private readonly string? sunshineExecutablePath;
    private string? sunshineLogPath;
    private Task sunshineWatchTask = Task.CompletedTask;
    private FileSystemWatcher? sunshineLogWatcher;
    private FileSystemWatcher? sunshineRecoveryWatcher;
    private FileSystemWatcher? pendingAudioRecoveryWatcher;
    private Task pendingAudioRecoveryTask = Task.CompletedTask;
    private bool pendingAudioRecoveryScheduled;
    private bool pendingAudioRecoveryRequested;
    private string? pendingAudioRecoveryCircuitBreakerFingerprint;
    private long? pendingAudioRecoveryLastDeviceEventRearmMilliseconds;
    private int pendingAudioRecoveryWatcherFailureRecorded;
    private CancellationTokenSource? sunshineScheduledRecoveryCancellation;
    private string? sunshineScheduledRecoveryTransaction;
    private TimeSpan sunshineScheduledRecoveryDelay;
    private bool sunshineScheduledRecoveryHasNoSessionProof;
    private bool sunshineScheduledRecoveryProofIsRetractable;
    private string? sunshineCurrentProcessTransaction;
    private int? sunshineActiveSessions;
    private bool sunshineLogSessionStateReliable;
    private long sunshineLogOffset;
    private string sunshineLogRemainder = string.Empty;
    private int sunshineLogReadScheduled;
    private int sunshineLogReadRequested;
    private int sunshineLogResetRequested;
    private int sunshineRecoveryInspectionScheduled;
    private int sunshineRecoveryInspectionRequested;
    private int sunshineLogObserverUnavailableRecorded;
    private CancellationTokenSource? resumeInspectionCancellation;
    private DateTimeOffset resumeObservationStartedAt;
    private DateTimeOffset resumeObservationEndsAt;
    private Stopwatch? resumeRecoveryRuntime;
    private string? resumePendingTransactionAtWake;
    private DisplaySuspendIntentState? currentSuspendIntent;
    private int actionRunning;
    private int suspendPending;
    private volatile bool disposed;

    internal HostRecoveryHotkeyWindow()
    {
        CreateHandle(new CreateParams { Caption = HostRecoveryAgentManager.WindowCaption });
        var modifiers = ModAlt | ModControl | ModShift | ModNoRepeat;
        try
        {
            var settings = HostSettings.Load();
            RegisterRequiredHotkey(RecoverDisplayHotkeyId, modifiers, VkF11, "display-recovery");
            if (HostRecoveryAgentManager.LegacyModeHotkeysRequired(settings))
            {
                RegisterOptionalModeHotkey(
                    Mode960x540HotkeyId,
                    modifiers,
                    VkF8,
                    new VitaDisplayMode(960, 540, 60));
                RegisterOptionalModeHotkey(
                    Mode960x544HotkeyId,
                    modifiers,
                    VkF9,
                    VitaDisplayModes.Native);
                RegisterOptionalModeHotkey(
                    Mode1280x720HotkeyId,
                    modifiers,
                    VkF10,
                    new VitaDisplayMode(1280, 720, 60));
            }
            InitializePendingAudioRecoveryWatcher();
            if (settings.HostMode.Equals(
                    "sunshine",
                    StringComparison.OrdinalIgnoreCase))
            {
                sunshineLifecycleEnabled = true;
                legacySunshineLogObserverEnabled =
                    !settings.IntegrateAllSunshineApps;
                sunshineExecutablePath =
                    StreamingHostLocator.FindSunshineExecutable();
                if (legacySunshineLogObserverEnabled)
                {
                    var configDirectory = SunshineConfigurator
                        .ResolveConfigurationDirectory(
                            settings.SunshineConfigDirectory,
                            "sunshine");
                    sunshineLogPath = ResolveSunshineLogPath(
                        configDirectory,
                        sunshineExecutablePath);
                }
                InitializeSunshineLifecycleWatchers();
                sunshineWatchTask = WatchSunshineProcessAsync(
                    sunshineWatchCancellation.Token);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmClose)
        {
            Application.ExitThread();
            return;
        }
        if (message.Msg == WmPowerBroadcast &&
            message.WParam.ToInt32() == PbtApmSuspend)
        {
            var durableSuspendFencePublished = false;
            Interlocked.Exchange(ref suspendPending, 1);
            StopResumeObservation();
            try
            {
                // Publish this cross-process fence before acknowledging the
                // power event. A session already inside its display
                // transaction checks it again after activation and restores
                // physical-only before returning.
                var replacementSuspendIntent =
                    DisplaySuspendIntentStore.BeginForCurrentAgent();
                Interlocked.Exchange(
                    ref currentSuspendIntent,
                    replacementSuspendIntent);
                durableSuspendFencePublished = true;
            }
            catch (Exception error)
            {
                HostRecoveryActions.StartUnfencedSuspendRecoveryFallback(
                    () => Volatile.Read(ref suspendPending) != 0);
                HostRecoveryActions.RecordUnhandledFailure(
                    "power-suspend-intent",
                    error);
            }
            if (TryAcquireSuspendAction())
            {
                try
                {
                    // This path is physical-topology-only even when lifecycle
                    // state is Disabled/corrupt. A surviving partial-pause
                    // agent must still prevent a stale VDD-only sleep layout.
                    HostRecoveryActions.PrepareDisplaysForSuspend(
                        SuspendDisplayLeaseBudget);
                }
                catch (Exception error)
                {
                    HostRecoveryActions.RecordUnhandledFailure(
                        "power-suspend-display-prepare",
                        error);
                }
                finally
                {
                    Interlocked.Exchange(ref actionRunning, 0);
                }
            }
            else
            {
                var competingAction = Volatile.Read(ref actionRunning);
                if (competingAction == 0 &&
                    Interlocked.CompareExchange(
                        ref actionRunning,
                        DisplayActionRunning,
                        0) == 0)
                {
                    try
                    {
                        HostRecoveryActions.PrepareDisplaysForSuspend(
                            SuspendDisplayLeaseBudget);
                    }
                    catch (Exception error)
                    {
                        HostRecoveryActions.RecordUnhandledFailure(
                            "power-suspend-display-prepare",
                            error);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref actionRunning, 0);
                    }
                }
                else
                {
                    HostRecoveryActions.RecordSuspendDecision(
                        true,
                        "A display rescue action was already running; it will honor the pending suspend before releasing the display transaction.");
                }
            }
            // PBT_APMSUSPEND cannot reliably be vetoed on current Windows,
            // but do not positively acknowledge it when durable publication
            // failed. The fallback above continues forcing physical-only
            // through the eventual resume boundary.
            message.Result = durableSuspendFencePublished
                ? new IntPtr(1)
                : IntPtr.Zero;
            return;
        }
        else if (message.Msg == WmPowerBroadcast &&
                 message.WParam.ToInt32() == PbtApmResumeAutomatic)
        {
            var recoveryScheduled = ScheduleResumeInspection(
                "power-resume",
                beginObservation: true);
            if (!recoveryScheduled)
            {
                HostRecoveryActions.RecordSuspendDecision(
                    false,
                    "Windows resumed, but physical verification could not be scheduled. The durable suspend fence remains active until startup recovery can prove a safe topology.");
            }
            Interlocked.Exchange(ref suspendPending, 0);
            message.Result = new IntPtr(1);
            return;
        }
        else if (message.Msg == WmDisplayChange)
        {
            RearmPendingAudioRecoveryForDeviceEvent();
            HandleTopologyNotification("resume-display-change");
        }
        else if (message.Msg == WmDeviceChange)
        {
            RearmPendingAudioRecoveryForDeviceEvent();
            HandleTopologyNotification("resume-device-change");
        }
        if (message.Msg == WmHotkey)
        {
            var hotkeyId = message.WParam.ToInt32();
            var actionKind = DisplayActionRunning;
            if (Interlocked.CompareExchange(
                    ref actionRunning,
                    actionKind,
                    0) != 0)
            {
                base.WndProc(ref message);
                return;
            }
            if (!BackendLifecycleManager.IsEnabled &&
                hotkeyId != RecoverDisplayHotkeyId)
            {
                Interlocked.Exchange(ref actionRunning, 0);
                return;
            }
            if (hotkeyId == RecoverDisplayHotkeyId || ModeForHotkeyId(hotkeyId) is not null)
            {
                StopResumeObservation();
            }
            _ = Task.Run(() =>
            {
                try
                {
                    if (hotkeyId == RecoverDisplayHotkeyId)
                    {
                        HostRecoveryActions.RecoverDisplayAndStreamingHost(
                            suspendIsPending: () =>
                                Volatile.Read(ref suspendPending) != 0);
                    }
                    else if (ModeForHotkeyId(hotkeyId) is { } mode)
                    {
                        HostRecoveryActions.ChangeVirtualDisplayMode(mode);
                    }
                }
                catch (Exception error)
                {
                    HostRecoveryActions.RecordUnhandledFailure(
                        "hotkey-action",
                        error);
                }
                finally
                {
                    try
                    {
                        if (actionKind == DisplayActionRunning &&
                            Volatile.Read(ref suspendPending) != 0)
                        {
                            HostRecoveryActions.PrepareDisplaysForSuspend(
                                SuspendDisplayLeaseBudget);
                        }
                    }
                    finally
                    {
                        Interlocked.Exchange(ref actionRunning, 0);
                    }
                }
            });
            return;
        }
        base.WndProc(ref message);
    }

    private void HandleTopologyNotification(string resumeTrigger)
    {
        var resumeObservationOpen = IsResumeObservationOpen();
        if (resumeObservationOpen)
        {
            ScheduleResumeInspection(
                resumeTrigger,
                beginObservation: false);
            return;
        }
        if (!sunshineLifecycleEnabled) return;
        var pendingTransaction = HostRecoveryActions
            .CapturePendingTransactionMarker();
        if (ShouldInspectSunshineRecoveryForTopologyEvent(
                resumeObservationOpen,
                pendingTransaction))
        {
            // Re-enter the existing generation/session classifier instead of
            // recovering directly from a noisy topology notification. This
            // safely re-arms a bounded retry after the earlier circuit breaker
            // while retaining the launch grace for a live/new session.
            ScheduleSunshineRecoveryInspection();
        }
    }

    internal static bool ShouldInspectSunshineRecoveryForTopologyEvent(
        bool resumeObservationOpen,
        string? pendingTransaction) =>
        !resumeObservationOpen &&
        !string.IsNullOrWhiteSpace(pendingTransaction);

    private bool TryAcquireSuspendAction()
    {
        var started = Environment.TickCount64;
        do
        {
            if (Interlocked.CompareExchange(
                    ref actionRunning,
                    DisplayActionRunning,
                    0) == 0)
            {
                return true;
            }
            Thread.Sleep(25);
        }
        while (Environment.TickCount64 - started < SuspendActionWaitMilliseconds);
        return false;
    }

    private async Task WatchSunshineProcessAsync(CancellationToken token)
    {
        var initialProcessObservationPending = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                using var sunshine = FindSunshineProcess();
                if (sunshine is null)
                {
                    if (initialProcessObservationPending)
                    {
                        initialProcessObservationPending = false;
                        var pendingTransaction = HostRecoveryActions
                            .CapturePendingTransactionMarker();
                        if (pendingTransaction is not null)
                        {
                            ScheduleSunshineRecovery(
                                pendingTransaction,
                                TimeSpan.Zero,
                                "Sunshine was absent when the lifecycle observer started",
                                hasNoSessionProof: true,
                                proofIsRetractable: false);
                        }
                    }
                    // Process discovery is the only polling path, and it runs
                    // only while Sunshine is absent.
                    await Task.Delay(SunshineAbsentPollInterval, token)
                        .ConfigureAwait(false);
                    continue;
                }
                initialProcessObservationPending = false;

                lock (sunshineLifecycleSync)
                {
                    sunshineCurrentProcessTransaction =
                        HostRecoveryActions.CapturePendingTransactionMarker();
                }
                try
                {
                    // Process.WaitForExitAsync registers an OS wait on this
                    // exact process handle. There is no polling while
                    // Sunshine is running.
                    await sunshine.WaitForExitAsync(token)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    await Task.Delay(SunshineAbsentPollInterval, token)
                        .ConfigureAwait(false);
                    continue;
                }

                token.ThrowIfCancellationRequested();
                string? exitedTransaction;
                lock (sunshineLifecycleSync)
                {
                    exitedTransaction = sunshineCurrentProcessTransaction;
                    sunshineCurrentProcessTransaction = null;
                    sunshineActiveSessions = null;
                }
                exitedTransaction ??= HostRecoveryActions
                    .CapturePendingTransactionMarker();
                if (exitedTransaction is not null)
                {
                    // A fast service restart does not invalidate the old
                    // generation. The recovery worker rechecks this exact
                    // marker under the display lease; a genuinely newer hook
                    // transaction is therefore never torn down.
                    ScheduleSunshineRecovery(
                        exitedTransaction,
                        TimeSpan.Zero,
                        "Sunshine process exit",
                        hasNoSessionProof: true,
                        proofIsRetractable: false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Normal agent shutdown.
        }
        catch (Exception error)
        {
            HostRecoveryActions.RecordUnhandledFailure(
                "sunshine-process-watch",
                error);
        }
    }

    private static string? ResolveSunshineLogPath(
        string configurationDirectory,
        string? sunshineExecutable)
    {
        try
        {
            var defaultPath = Path.GetFullPath(Path.Combine(
                configurationDirectory,
                "sunshine.log"));
            var configurationPath = Path.Combine(
                configurationDirectory,
                "sunshine.conf");
            string? configuredLogPath = null;
            if (File.Exists(configurationPath))
            {
                using var stream = new FileStream(
                    configurationPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaximumSunshineConfigurationBytes)
                {
                    return null;
                }
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                while (reader.ReadLine() is { } line)
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 ||
                        trimmed.StartsWith('#'))
                    {
                        continue;
                    }
                    var separator = trimmed.IndexOf('=');
                    if (separator <= 0 ||
                        !trimmed[..separator].Trim().Equals(
                            "log_path",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    configuredLogPath = trimmed[(separator + 1)..]
                        .Trim()
                        .Trim('"');
                }
            }
            if (string.IsNullOrWhiteSpace(configuredLogPath))
            {
                return defaultPath;
            }
            if (Path.IsPathRooted(configuredLogPath))
            {
                return Path.GetFullPath(configuredLogPath);
            }

            var candidates = new List<string>
            {
                Path.GetFullPath(Path.Combine(
                    configurationDirectory,
                    configuredLogPath)),
            };
            var executableDirectory = string.IsNullOrWhiteSpace(
                sunshineExecutable)
                ? null
                : Path.GetDirectoryName(sunshineExecutable);
            if (!string.IsNullOrWhiteSpace(executableDirectory))
            {
                candidates.Add(Path.GetFullPath(Path.Combine(
                    executableDirectory,
                    configuredLogPath)));
            }
            var existing = candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToArray();
            if (existing.Length == 1)
            {
                return existing[0];
            }
            if (configuredLogPath.Equals(
                    "sunshine.log",
                    StringComparison.OrdinalIgnoreCase) &&
                existing.Length == 0)
            {
                return defaultPath;
            }
            // A custom relative path with no unique existing target is
            // ambiguous. Do not tail an unproven file or infer a timeout from
            // it; exact Sunshine-process-exit and suspend recovery remain
            // armed for the pending transaction.
            return null;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException or
                System.Security.SecurityException)
        {
            return null;
        }
    }

    private void InitializePendingAudioRecoveryWatcher()
    {
        FileSystemWatcher? watcher = null;
        try
        {
            var directory = Path.GetDirectoryName(
                HostStatePaths.AudioRecoveryFile);
            if (!string.IsNullOrWhiteSpace(directory) &&
                Directory.Exists(directory))
            {
                watcher = new FileSystemWatcher(
                    directory,
                    "audio-recovery*.json")
                {
                    NotifyFilter = NotifyFilters.FileName |
                                   NotifyFilters.CreationTime |
                                   NotifyFilters.LastWrite |
                                   NotifyFilters.Size,
                    IncludeSubdirectories = false,
                };
                watcher.Created += OnPendingAudioRecoveryFileChanged;
                watcher.Changed += OnPendingAudioRecoveryFileChanged;
                watcher.Renamed += OnPendingAudioRecoveryFileRenamed;
                watcher.Error += OnPendingAudioRecoveryWatcherError;
                lock (pendingAudioRecoverySync)
                {
                    pendingAudioRecoveryWatcher = watcher;
                }
                watcher.EnableRaisingEvents = true;
                watcher = null;
            }
        }
        catch (Exception error) when (IsOperationalAudioRetryError(error))
        {
            watcher?.Dispose();
            lock (pendingAudioRecoverySync)
            {
                pendingAudioRecoveryWatcher?.Dispose();
                pendingAudioRecoveryWatcher = null;
            }
            RecordPendingAudioRecoveryWatcherFailure(error);
        }

        // Startup recovery is asynchronous and completely dormant when both
        // protected records are absent. The watcher is the only steady-state
        // mechanism; no timer or polling loop exists without an obligation.
        SchedulePendingAudioRecovery();
    }

    private void OnPendingAudioRecoveryFileChanged(
        object sender,
        FileSystemEventArgs args)
    {
        if (IsPendingAudioRecoveryFileName(args.Name))
        {
            SchedulePendingAudioRecovery();
        }
    }

    private void OnPendingAudioRecoveryFileRenamed(
        object sender,
        RenamedEventArgs args)
    {
        if (ShouldSchedulePendingAudioRecoveryForRename(
                args.Name,
                args.OldName))
        {
            SchedulePendingAudioRecovery();
        }
    }

    private void RearmPendingAudioRecoveryForDeviceEvent()
    {
        if (disposed || !PendingAudioRecoveryRecordExists()) return;

        var schedule = false;
        lock (pendingAudioRecoverySync)
        {
            var nowMilliseconds = Environment.TickCount64;
            if (ShouldRearmPendingAudioRecoveryForDeviceEvent(
                    hasPendingRecord: true,
                    circuitBreakerFingerprint:
                        pendingAudioRecoveryCircuitBreakerFingerprint,
                    nowMilliseconds: nowMilliseconds,
                    lastRearmMilliseconds:
                        pendingAudioRecoveryLastDeviceEventRearmMilliseconds))
            {
                // Only an actual Windows topology/device notification may
                // release an unchanged durable fingerprint from the breaker.
                // FileSystemWatcher callbacks never enter this path, so the
                // worker's own partial-progress write cannot extend its
                // lifetime. Clearing before scheduling also coalesces the
                // rest of this notification burst.
                pendingAudioRecoveryCircuitBreakerFingerprint = null;
                pendingAudioRecoveryLastDeviceEventRearmMilliseconds =
                    nowMilliseconds;
                schedule = true;
            }
        }

        if (schedule)
        {
            // Scheduling is intentionally outside WndProc's display/resume
            // decision path. No Core Audio call or endpoint wait occurs on
            // the window-message thread.
            _ = Task.Run(SchedulePendingAudioRecovery);
        }
    }

    private void OnPendingAudioRecoveryWatcherError(
        object sender,
        ErrorEventArgs args)
    {
        if (disposed) return;
        RecordPendingAudioRecoveryWatcherFailure(
            args.GetException() ??
            new IOException(
                "The pending audio endpoint watcher stopped unexpectedly."));
        // A record already on disk still receives its bounded attempt even if
        // future filesystem notifications are unavailable.
        SchedulePendingAudioRecovery();
    }

    private void RecordPendingAudioRecoveryWatcherFailure(Exception error)
    {
        if (Interlocked.CompareExchange(
                ref pendingAudioRecoveryWatcherFailureRecorded,
                1,
                0) != 0)
        {
            return;
        }
        HostRecoveryActions.RecordPendingAudioRecoveryDecision(
            false,
            "The event-driven pending audio recovery watcher became " +
            $"unavailable: {error.Message}. Existing pending state received " +
            "one bounded startup attempt; restart the host agent after " +
            "repairing state-directory access to re-arm file notifications.");
    }

    private void SchedulePendingAudioRecovery()
    {
        if (disposed ||
            pendingAudioRecoveryCancellation.IsCancellationRequested)
        {
            return;
        }

        var fingerprint = CapturePendingAudioRecoveryFingerprint();
        lock (pendingAudioRecoverySync)
        {
            if (disposed ||
                pendingAudioRecoveryCancellation.IsCancellationRequested ||
                !ShouldArmPendingAudioRecoveryRetry(
                    fingerprint,
                    pendingAudioRecoveryCircuitBreakerFingerprint))
            {
                return;
            }

            pendingAudioRecoveryRequested = true;
            if (pendingAudioRecoveryScheduled)
            {
                return;
            }

            pendingAudioRecoveryRequested = false;
            pendingAudioRecoveryScheduled = true;
            // A new durable fingerprint is a new obligation. An event caused
            // by this worker's own partial-progress write retains the same
            // active deadline and is latched at the breaker below.
            pendingAudioRecoveryCircuitBreakerFingerprint = null;
            pendingAudioRecoveryTask = Task.Run(
                () => RunPendingAudioRecoveryAsync(
                    pendingAudioRecoveryCancellation.Token));
        }
    }

    private async Task RunPendingAudioRecoveryAsync(
        CancellationToken token)
    {
        var runtime = Stopwatch.StartNew();
        var attempts = 0;
        AudioEndpointRestoreResult? lastResult = null;
        Exception? lastError = null;
        try
        {
            // SavePending is normally followed by an immediate in-transaction
            // restore. Coalescing its primary/backup write burst here avoids a
            // competing Core Audio call and guarantees this worker starts
            // after the display transaction has had time to release.
            await Task.Delay(
                    PendingAudioRecoveryInitialDelay,
                    token)
                .ConfigureAwait(false);

            while (!disposed && !token.IsCancellationRequested)
            {
                var fingerprint =
                    CapturePendingAudioRecoveryFingerprint();
                if (fingerprint is null)
                {
                    return;
                }
                var hasPendingRecord = true;
                if (!ShouldContinuePendingAudioRecoveryRetry(
                        attempts,
                        runtime.Elapsed,
                        succeeded: false,
                        hasPendingRecord: hasPendingRecord,
                        cancellationRequested:
                            token.IsCancellationRequested))
                {
                    OpenPendingAudioRecoveryCircuitBreaker(
                        fingerprint,
                        attempts,
                        lastResult,
                        lastError,
                        displaySessionStillActive:
                            File.Exists(HostStatePaths.RecoveryFile));
                    return;
                }

                // The display recovery record is also the live-session fence.
                // Never race SessionManager's SavePending/RestorePending pair,
                // and never spend Core Audio work on an endpoint hidden by an
                // active Vita display handoff. This delay owns no display or
                // session lock.
                if (File.Exists(HostStatePaths.RecoveryFile))
                {
                    await Task.Delay(
                            PendingAudioRecoveryRetryInterval,
                            token)
                        .ConfigureAwait(false);
                    continue;
                }

                attempts++;
                try
                {
                    lastResult = AudioEndpointRecoveryService.RestorePending(
                        waitForEndpoint: false);
                    lastError = null;
                }
                catch (Exception error) when (
                    IsOperationalAudioRetryError(error))
                {
                    lastResult = null;
                    lastError = error;
                }

                if (lastResult?.Succeeded == true)
                {
                    lock (pendingAudioRecoverySync)
                    {
                        pendingAudioRecoveryCircuitBreakerFingerprint = null;
                    }
                    HostRecoveryActions.RecordPendingAudioRecoveryDecision(
                        true,
                        "Windows reconnected the exact pre-stream audio " +
                        $"endpoint defaults after {attempts} asynchronous " +
                        $"attempt{(attempts == 1 ? string.Empty : "s")}.");
                    return;
                }

                fingerprint = CapturePendingAudioRecoveryFingerprint();
                hasPendingRecord = fingerprint is not null;
                if (!ShouldContinuePendingAudioRecoveryRetry(
                        attempts,
                        runtime.Elapsed,
                        succeeded: false,
                        hasPendingRecord: hasPendingRecord,
                        cancellationRequested:
                            token.IsCancellationRequested))
                {
                    if (fingerprint is not null)
                    {
                        OpenPendingAudioRecoveryCircuitBreaker(
                            fingerprint,
                            attempts,
                            lastResult,
                            lastError,
                            displaySessionStillActive: false);
                    }
                    return;
                }

                await Task.Delay(
                        PendingAudioRecoveryRetryInterval,
                        token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposal is the normal cancellation path.
        }
        finally
        {
            bool inspectRacingNotification;
            lock (pendingAudioRecoverySync)
            {
                pendingAudioRecoveryScheduled = false;
                inspectRacingNotification =
                    pendingAudioRecoveryRequested &&
                    !disposed &&
                    !token.IsCancellationRequested;
                pendingAudioRecoveryRequested = false;
            }
            if (inspectRacingNotification)
            {
                // A self-write from partial progress resolves to the exact
                // fingerprint latched by the breaker and therefore cannot
                // create a fresh 60-second retry window.
                SchedulePendingAudioRecovery();
            }
        }
    }

    private void OpenPendingAudioRecoveryCircuitBreaker(
        string fingerprint,
        int attempts,
        AudioEndpointRestoreResult? lastResult,
        Exception? lastError,
        bool displaySessionStillActive)
    {
        lock (pendingAudioRecoverySync)
        {
            pendingAudioRecoveryCircuitBreakerFingerprint = fingerprint;
        }
        var detail = lastError?.Message ??
            lastResult?.Detail ??
            (displaySessionStillActive
                ? "A Vita display session remained active for the full retry window."
                : "The exact endpoint remained unavailable.");
        HostRecoveryActions.RecordPendingAudioRecoveryDecision(
            false,
            "Pending audio endpoint recovery reached its 60-second circuit " +
            $"breaker after {attempts} Core Audio attempt" +
            $"{(attempts == 1 ? string.Empty : "s")}. {detail} " +
            "The protected exact endpoint record was retained; a later " +
            "record change or agent restart can re-arm recovery.");
    }

    private static string? CapturePendingAudioRecoveryFingerprint()
    {
        if (!PendingAudioRecoveryRecordExists()) return null;
        try
        {
            var pending = AudioEndpointRecoveryService.InspectPending();
            return pending is null
                ? null
                : "valid:" + JsonSerializer.Serialize(pending);
        }
        catch (Exception error) when (IsOperationalAudioRetryError(error))
        {
            // A damaged record must still be circuit-bounded. Metadata is a
            // fallback identity only; the recovery service remains the sole
            // parser and authority for protected endpoint state.
            return "unreadable:" + string.Join(
                "|",
                CapturePendingAudioRecoveryFileIdentity(
                    HostStatePaths.AudioRecoveryFile),
                CapturePendingAudioRecoveryFileIdentity(
                    HostStatePaths.AudioRecoveryBackupFile));
        }
    }

    private static string CapturePendingAudioRecoveryFileIdentity(
        string path)
    {
        try
        {
            var information = new FileInfo(path);
            information.Refresh();
            return information.Exists
                ? $"present:{information.Length}:" +
                  information.LastWriteTimeUtc.Ticks
                : "missing";
        }
        catch (Exception error) when (IsOperationalAudioRetryError(error))
        {
            return "unreadable";
        }
    }

    private static bool PendingAudioRecoveryRecordExists() =>
        File.Exists(HostStatePaths.AudioRecoveryFile) ||
        File.Exists(HostStatePaths.AudioRecoveryBackupFile);

    private static bool IsPendingAudioRecoveryFileName(string? name) =>
        string.Equals(
            name,
            Path.GetFileName(HostStatePaths.AudioRecoveryFile),
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            name,
            Path.GetFileName(HostStatePaths.AudioRecoveryBackupFile),
            StringComparison.OrdinalIgnoreCase);

    internal static bool ShouldSchedulePendingAudioRecoveryForRename(
        string? name,
        string? oldName) =>
        IsPendingAudioRecoveryFileName(name) ||
        IsPendingAudioRecoveryFileName(oldName);

    internal static bool IsPendingAudioRecoveryFileNameForContractTest(
        string? name) =>
        IsPendingAudioRecoveryFileName(name);

    internal static bool ShouldArmPendingAudioRecoveryRetry(
        string? currentFingerprint,
        string? circuitBreakerFingerprint) =>
        currentFingerprint is not null &&
        !string.Equals(
            currentFingerprint,
            circuitBreakerFingerprint,
            StringComparison.Ordinal);

    internal static bool ShouldContinuePendingAudioRecoveryRetry(
        int attempts,
        TimeSpan elapsed,
        bool succeeded,
        bool hasPendingRecord,
        bool cancellationRequested) =>
        !succeeded &&
        hasPendingRecord &&
        !cancellationRequested &&
        attempts < MaximumPendingAudioRecoveryAttempts &&
        elapsed < PendingAudioRecoveryBudget;

    internal static bool ShouldRearmPendingAudioRecoveryForDeviceEvent(
        bool hasPendingRecord,
        string? circuitBreakerFingerprint,
        long nowMilliseconds,
        long? lastRearmMilliseconds) =>
        hasPendingRecord &&
        circuitBreakerFingerprint is not null &&
        (lastRearmMilliseconds is null ||
         nowMilliseconds - lastRearmMilliseconds.Value >=
         PendingAudioRecoveryDeviceEventCooldown.TotalMilliseconds);

    internal static int MaximumPendingAudioRecoveryAttemptsForContractTest =>
        MaximumPendingAudioRecoveryAttempts;

    internal static TimeSpan PendingAudioRecoveryBudgetForContractTest =>
        PendingAudioRecoveryBudget;

    internal static TimeSpan
        PendingAudioRecoveryDeviceEventCooldownForContractTest =>
        PendingAudioRecoveryDeviceEventCooldown;

    private static bool IsOperationalAudioRetryError(Exception error) =>
        error is COMException or
            ExternalException or
            IOException or
            ArgumentException or
            InvalidCastException or
            InvalidDataException or
            InvalidOperationException or
            PlatformNotSupportedException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            System.Security.SecurityException;

    private void InitializeSunshineLifecycleWatchers()
    {
        var recoveryDirectory = Path.GetDirectoryName(
            HostStatePaths.RecoveryFile);
        if (!string.IsNullOrWhiteSpace(recoveryDirectory) &&
            Directory.Exists(recoveryDirectory))
        {
            sunshineRecoveryWatcher = new FileSystemWatcher(
                recoveryDirectory,
                Path.GetFileName(HostStatePaths.RecoveryFile))
            {
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.CreationTime |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
                IncludeSubdirectories = false,
            };
            sunshineRecoveryWatcher.Created += OnSunshineRecoveryFileChanged;
            sunshineRecoveryWatcher.Changed += OnSunshineRecoveryFileChanged;
            sunshineRecoveryWatcher.Deleted += OnSunshineRecoveryFileChanged;
            sunshineRecoveryWatcher.Renamed += OnSunshineRecoveryFileRenamed;
            sunshineRecoveryWatcher.Error += OnSunshineLifecycleWatcherError;
        }

        if (sunshineRecoveryWatcher is not null)
        {
            sunshineRecoveryWatcher.EnableRaisingEvents = true;
        }
        ScheduleSunshineRecoveryInspection();
    }

    private bool ArmSunshineLogWatcher()
    {
        lock (sunshineLifecycleSync)
        {
            if (sunshineLogWatcher is not null) return true;
            if (disposed ||
                sunshineWatchCancellation.IsCancellationRequested ||
                string.IsNullOrWhiteSpace(sunshineLogPath))
            {
                return false;
            }
            var logDirectory = Path.GetDirectoryName(sunshineLogPath);
            if (string.IsNullOrWhiteSpace(logDirectory) ||
                !Directory.Exists(logDirectory))
            {
                return false;
            }

            var watcher = new FileSystemWatcher(
                logDirectory,
                Path.GetFileName(sunshineLogPath))
            {
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.CreationTime |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size,
                IncludeSubdirectories = false,
                InternalBufferSize = 8192,
            };
            watcher.Created += OnSunshineLogCreated;
            watcher.Changed += OnSunshineLogChanged;
            watcher.Deleted += OnSunshineLogCreated;
            watcher.Renamed += OnSunshineLogRenamed;
            watcher.Error += OnSunshineLifecycleWatcherError;
            sunshineLogWatcher = watcher;
            ReadSunshineLogBaseline();
            watcher.EnableRaisingEvents = true;
        }
        ScheduleSunshineLogRead(reset: false);
        return true;
    }

    private void DisarmSunshineLogWatcher()
    {
        FileSystemWatcher? watcher;
        lock (sunshineLifecycleSync)
        {
            watcher = sunshineLogWatcher;
            sunshineLogWatcher = null;
            sunshineActiveSessions = null;
            sunshineLogSessionStateReliable = false;
            sunshineLogOffset = 0;
            sunshineLogRemainder = string.Empty;
        }
        watcher?.Dispose();
    }

    private void OnSunshineRecoveryFileChanged(
        object sender,
        FileSystemEventArgs args) =>
        ScheduleSunshineRecoveryInspection();

    private void OnSunshineRecoveryFileRenamed(
        object sender,
        RenamedEventArgs args) =>
        ScheduleSunshineRecoveryInspection();

    /*
     * This requested-plus-scheduled drain intentionally mirrors the log-tail
     * reader below. FileSystemWatcher does not queue reliable one-for-one
     * notifications; correctness comes from re-reading durable state after
     * every observed edge, including an edge that races worker teardown.
     */
    private void ScheduleSunshineRecoveryInspection()
    {
        if (disposed ||
            sunshineWatchCancellation.IsCancellationRequested)
        {
            return;
        }
        Interlocked.Exchange(
            ref sunshineRecoveryInspectionRequested,
            1);
        if (Interlocked.CompareExchange(
                ref sunshineRecoveryInspectionScheduled,
                1,
                0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!disposed &&
                       !sunshineWatchCancellation.IsCancellationRequested &&
                       Interlocked.Exchange(
                           ref sunshineRecoveryInspectionRequested,
                           0) != 0)
                {
                    // Coalesce the create/write/rename burst produced by one
                    // atomic journal publication without dropping a later
                    // generation that arrives while this inspection runs.
                    await Task.Delay(
                            TimeSpan.FromMilliseconds(100),
                            sunshineWatchCancellation.Token)
                        .ConfigureAwait(false);
                    InspectSunshineRecoveryMarker();
                }
            }
            catch (OperationCanceledException) when (
                sunshineWatchCancellation.IsCancellationRequested)
            {
                // Normal shutdown.
            }
            finally
            {
                Interlocked.Exchange(
                    ref sunshineRecoveryInspectionScheduled,
                    0);
                // Close the event-arrived-between-last-exchange-and-clear
                // race. The next worker again drains every requested edge.
                if (!disposed &&
                    !sunshineWatchCancellation.IsCancellationRequested &&
                    Volatile.Read(
                        ref sunshineRecoveryInspectionRequested) != 0)
                {
                    ScheduleSunshineRecoveryInspection();
                }
            }
        });
    }

    private void InspectSunshineRecoveryMarker()
    {
        var marker = HostRecoveryActions.CapturePendingTransactionMarker();
        lock (sunshineLifecycleSync)
        {
            sunshineCurrentProcessTransaction = marker;
        }
        if (marker is null)
        {
            CancelScheduledSunshineRecovery();
            DisarmSunshineLogWatcher();
            HostRecoveryActions.DiscardStaleStreamBoundaryLease();
            return;
        }

        // The exact authenticated client/generation lease is authoritative.
        // The supported all-app path intentionally does not tail Sunshine's
        // log: lease expiry and an exact Sunshine process exit are sufficient
        // to recover, without forcing INFO logging or processing every global
        // client lifecycle record.
        ScheduleSunshineRecovery(
            marker,
            HostRecoveryActions.GetStreamBoundaryRecoveryDelay(marker),
            "authenticated Vita stream lease expired",
            hasNoSessionProof: false,
            proofIsRetractable: false);
        if (!legacySunshineLogObserverEnabled)
        {
            return;
        }
        if (!ArmSunshineLogWatcher())
        {
            if (Interlocked.CompareExchange(
                    ref sunshineLogObserverUnavailableRecorded,
                    1,
                    0) == 0)
            {
                HostRecoveryActions.RecordSunshineExitDecision(
                    false,
                    "A Vita display transaction appeared, but Sunshine's configured session log path was missing or ambiguous. The observer left the active transaction unchanged; process-exit recovery remains armed. Repair Sunshine logging before relying on disconnect cleanup.");
            }
            return;
        }
        Interlocked.Exchange(
            ref sunshineLogObserverUnavailableRecorded,
            0);
    }

    private void OnSunshineLogChanged(
        object sender,
        FileSystemEventArgs args) =>
        ScheduleSunshineLogRead(reset: false);

    private void OnSunshineLogCreated(
        object sender,
        FileSystemEventArgs args) =>
        ScheduleSunshineLogRead(reset: true);

    private void OnSunshineLogRenamed(
        object sender,
        RenamedEventArgs args) =>
        ScheduleSunshineLogRead(reset: true);

    private void OnSunshineLifecycleWatcherError(
        object sender,
        ErrorEventArgs args)
    {
        if (HostRecoveryActions.CapturePendingTransactionMarker() is null)
        {
            return;
        }
        lock (sunshineLifecycleSync)
        {
            sunshineLogSessionStateReliable = false;
        }
        ScheduleSunshineRecoveryInspection();
        if (legacySunshineLogObserverEnabled)
        {
            ScheduleSunshineLogRead(reset: true);
        }
        if (Interlocked.CompareExchange(
                ref sunshineLogObserverUnavailableRecorded,
                1,
                0) == 0)
        {
            HostRecoveryActions.RecordSunshineExitDecision(
                false,
                $"Sunshine lifecycle filesystem notification became unavailable: {args.GetException()?.Message ?? "unknown watcher error"}. The observer left any active transaction unchanged; process-exit recovery remains armed.");
        }
    }

    private void ScheduleSunshineLogRead(bool reset)
    {
        if (disposed || sunshineWatchCancellation.IsCancellationRequested)
        {
            return;
        }
        if (HostRecoveryActions.CapturePendingTransactionMarker() is null)
        {
            return;
        }
        if (reset)
        {
            Interlocked.Exchange(ref sunshineLogResetRequested, 1);
        }
        Interlocked.Exchange(ref sunshineLogReadRequested, 1);
        if (Interlocked.CompareExchange(
                ref sunshineLogReadScheduled,
                1,
                0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                while (!disposed &&
                       !sunshineWatchCancellation.IsCancellationRequested &&
                       HostRecoveryActions.CapturePendingTransactionMarker() is not null &&
                       Interlocked.Exchange(
                           ref sunshineLogReadRequested,
                           0) != 0)
                {
                    ReadAppendedSunshineLog();
                }
            }
            catch (Exception error) when (
                error is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    System.ComponentModel.Win32Exception)
            {
                lock (sunshineLifecycleSync)
                {
                    sunshineLogSessionStateReliable = false;
                }
                if (Interlocked.CompareExchange(
                        ref sunshineLogObserverUnavailableRecorded,
                        1,
                        0) == 0)
                {
                    HostRecoveryActions.RecordSunshineExitDecision(
                        false,
                        $"Sunshine's session log could not be read: {error.Message}. The observer left any active transaction unchanged; process-exit recovery remains armed.");
                }
            }
            finally
            {
                Interlocked.Exchange(ref sunshineLogReadScheduled, 0);
                if (Volatile.Read(ref sunshineLogReadRequested) != 0)
                {
                    ScheduleSunshineLogRead(reset: false);
                }
            }
        });
    }

    private void ReadSunshineLogBaseline()
    {
        if (string.IsNullOrWhiteSpace(sunshineLogPath) ||
            !File.Exists(sunshineLogPath))
        {
            return;
        }
        var snapshot = ReadSunshineLogRange(
            sunshineLogPath,
            startAt: null,
            out var length,
            out var beganMidFile);
        sunshineLogOffset = length;
        sunshineLogRemainder = ExtractCompleteLogLines(
            snapshot,
            beganMidFile,
            out var lines);

        var currentProcessStart = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            if (lines[index].Contains(
                    "Sunshine version",
                    StringComparison.OrdinalIgnoreCase))
            {
                currentProcessStart = index;
            }
        }
        int? reconstructedSessions = null;
        for (var index = Math.Max(0, currentProcessStart);
             index < lines.Count;
             index++)
        {
            ApplySunshineSessionLine(
                lines[index],
                ref reconstructedSessions,
                scheduleRecovery: false);
        }
        lock (sunshineLifecycleSync)
        {
            sunshineActiveSessions = reconstructedSessions;
            sunshineLogSessionStateReliable =
                !beganMidFile || currentProcessStart >= 0;
            sunshineCurrentProcessTransaction = HostRecoveryActions
                .CapturePendingTransactionMarker();
        }
    }

    private void ReadAppendedSunshineLog()
    {
        if (string.IsNullOrWhiteSpace(sunshineLogPath) ||
            !File.Exists(sunshineLogPath))
        {
            return;
        }
        var reset = Interlocked.Exchange(
            ref sunshineLogResetRequested,
            0) != 0;
        var currentLength = new FileInfo(sunshineLogPath).Length;
        if (reset || currentLength < sunshineLogOffset)
        {
            sunshineLogOffset = 0;
            sunshineLogRemainder = string.Empty;
            lock (sunshineLifecycleSync)
            {
                sunshineActiveSessions = null;
                sunshineLogSessionStateReliable = false;
            }
        }
        if (currentLength == sunshineLogOffset)
        {
            return;
        }

        var snapshot = ReadSunshineLogRange(
            sunshineLogPath,
            sunshineLogOffset,
            out var length,
            out var beganMidFile);
        sunshineLogOffset = length;
        var combined = beganMidFile
            ? snapshot
            : sunshineLogRemainder + snapshot;
        if (beganMidFile)
        {
            lock (sunshineLifecycleSync)
            {
                sunshineActiveSessions = null;
                sunshineLogSessionStateReliable = false;
            }
        }
        sunshineLogRemainder = ExtractCompleteLogLines(
            combined,
            beganMidFile,
            out var lines);
        foreach (var line in lines)
        {
            HandleSunshineSessionLine(line);
        }
    }

    private static string ReadSunshineLogRange(
        string path,
        long? startAt,
        out long length,
        out bool beganMidFile)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        length = stream.Length;
        var requestedStart = startAt ?? Math.Max(
            0,
            length - MaximumSunshineLogTailBytes);
        var actualStart = Math.Max(
            requestedStart,
            length - MaximumSunshineLogTailBytes);
        beganMidFile = actualStart > 0 && actualStart != startAt;
        stream.Position = actualStart;
        var bytesToRead = checked((int)Math.Min(
            MaximumSunshineLogTailBytes,
            length - actualStart));
        var buffer = new byte[bytesToRead];
        var read = 0;
        while (read < buffer.Length)
        {
            var received = stream.Read(buffer, read, buffer.Length - read);
            if (received == 0) break;
            read += received;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string ExtractCompleteLogLines(
        string content,
        bool discardFirstPartial,
        out IReadOnlyList<string> lines)
    {
        var split = content.Split('\n');
        var completeCount = content.EndsWith('\n')
            ? split.Length - 1
            : split.Length - 1;
        var first = discardFirstPartial && completeCount > 0 ? 1 : 0;
        lines = split
            .Skip(first)
            .Take(Math.Max(0, completeCount - first))
            .Select(line => line.TrimEnd('\r'))
            .ToArray();
        return content.EndsWith('\n')
            ? string.Empty
            : split[^1];
    }

    private void HandleSunshineSessionLine(string line)
    {
        int? activeSessions;
        lock (sunshineLifecycleSync)
        {
            activeSessions = sunshineActiveSessions;
        }
        ApplySunshineSessionLine(
            line,
            ref activeSessions,
            scheduleRecovery: true);
        lock (sunshineLifecycleSync)
        {
            sunshineActiveSessions = activeSessions;
        }
    }

    private void ApplySunshineSessionLine(
        string line,
        ref int? activeSessions,
        bool scheduleRecovery)
    {
        if (line.Contains(
                "Sunshine version",
                StringComparison.OrdinalIgnoreCase))
        {
            activeSessions = null;
            lock (sunshineLifecycleSync)
            {
                sunshineCurrentProcessTransaction = null;
                sunshineLogSessionStateReliable = true;
            }
            return;
        }
        if (TryReadSunshineActiveSessionCount(line, out var reported))
        {
            activeSessions = reported;
            var startMarker = HostRecoveryActions
                .CapturePendingTransactionMarker();
            lock (sunshineLifecycleSync)
            {
                sunshineCurrentProcessTransaction = startMarker;
                sunshineLogSessionStateReliable = true;
            }
            // A Sunshine-wide count includes unrelated Moonlight clients.
            // Never cancel the exact Vita lease deadline because it is
            // nonzero. It does invalidate an earlier global-zero proof,
            // though, so downgrade only that proof back to the lease timer.
            if (scheduleRecovery && reported > 0 && startMarker is not null)
            {
                ReconcileSunshineRecoveryAfterSessionStart(startMarker);
            }
            return;
        }
        if (!line.Contains(
                "CLIENT DISCONNECTED",
                StringComparison.OrdinalIgnoreCase) ||
            activeSessions is not > 0)
        {
            return;
        }

        activeSessions--;
        if (!scheduleRecovery || activeSessions != 0)
        {
            return;
        }
        string? disconnectMarker;
        lock (sunshineLifecycleSync)
        {
            disconnectMarker = sunshineCurrentProcessTransaction;
        }
        disconnectMarker ??= HostRecoveryActions
            .CapturePendingTransactionMarker();
        if (disconnectMarker is not null)
        {
            ScheduleSunshineRecovery(
                disconnectMarker,
                SunshineDisconnectGrace,
                "last Sunshine client disconnect",
                hasNoSessionProof: true,
                proofIsRetractable: true);
        }
    }

    private static bool TryReadSunshineActiveSessionCount(
        string line,
        out int count)
    {
        count = 0;
        const string prefix =
            "New streaming session started [active sessions:";
        var start = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += prefix.Length;
        var end = line.IndexOf(']', start);
        if (end < 0) return false;
        return int.TryParse(line[start..end].Trim(), out count) &&
               count >= 0;
    }

    private void ScheduleSunshineRecovery(
        string expectedTransaction,
        TimeSpan delay,
        string reason,
        bool hasNoSessionProof,
        bool proofIsRetractable)
    {
        if (disposed ||
            sunshineWatchCancellation.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(expectedTransaction))
        {
            return;
        }
        var cancellation = CancellationTokenSource
            .CreateLinkedTokenSource(sunshineWatchCancellation.Token);
        lock (sunshineLifecycleSync)
        {
            if (sunshineScheduledRecoveryCancellation is not null &&
                string.Equals(
                    sunshineScheduledRecoveryTransaction,
                    expectedTransaction,
                    StringComparison.Ordinal) &&
                sunshineScheduledRecoveryDelay <= delay &&
                (sunshineScheduledRecoveryHasNoSessionProof ||
                 !hasNoSessionProof))
            {
                cancellation.Dispose();
                return;
            }
            sunshineScheduledRecoveryCancellation?.Cancel();
            sunshineScheduledRecoveryCancellation = cancellation;
            sunshineScheduledRecoveryTransaction = expectedTransaction;
            sunshineScheduledRecoveryDelay = delay;
            sunshineScheduledRecoveryHasNoSessionProof =
                hasNoSessionProof;
            sunshineScheduledRecoveryProofIsRetractable =
                proofIsRetractable;
        }
        _ = Task.Run(() => RunScheduledSunshineRecoveryAsync(
            expectedTransaction,
            delay,
            reason,
            hasNoSessionProof,
            cancellation));
    }

    private async Task RunScheduledSunshineRecoveryAsync(
        string expectedTransaction,
        TimeSpan delay,
        string reason,
        bool hasNoSessionProof,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
            Stopwatch? recoveryRuntime = null;
            while (true)
            {
                var liveLeaseDelay = hasNoSessionProof
                    ? TimeSpan.Zero
                    : HostRecoveryActions
                        .GetStreamBoundaryRecoveryDelay(
                            expectedTransaction);
                if (liveLeaseDelay > TimeSpan.Zero)
                {
                    recoveryRuntime = null;
                    await Task.Delay(liveLeaseDelay, token)
                        .ConfigureAwait(false);
                    continue;
                }
                if (TryRecoverAfterSunshineExit(
                        expectedTransaction,
                        reason,
                        hasNoSessionProof,
                        token))
                {
                    return;
                }
                recoveryRuntime ??= Stopwatch.StartNew();
                if (recoveryRuntime.Elapsed >= SunshineExitRecoveryBudget)
                {
                    HostRecoveryActions.RecordSunshineExitDecision(
                        false,
                        $"{reason} left a Vita display transaction, but automatic idle recovery either failed or could not acquire the display transaction during the bounded 30-second cleanup window. Automatic cleanup stopped without changing that transaction.");
                    return;
                }
                await Task.Delay(SunshineRecoveryRetryInterval, token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A reconnect, newer marker, or agent shutdown superseded this
            // exact recovery generation.
        }
        finally
        {
            lock (sunshineLifecycleSync)
            {
                if (ReferenceEquals(
                        sunshineScheduledRecoveryCancellation,
                        cancellation))
                {
                    sunshineScheduledRecoveryCancellation = null;
                    sunshineScheduledRecoveryTransaction = null;
                    sunshineScheduledRecoveryDelay = TimeSpan.Zero;
                    sunshineScheduledRecoveryHasNoSessionProof = false;
                    sunshineScheduledRecoveryProofIsRetractable = false;
                }
            }
            cancellation.Dispose();
        }
    }

    private void CancelScheduledSunshineRecovery()
    {
        lock (sunshineLifecycleSync)
        {
            sunshineScheduledRecoveryCancellation?.Cancel();
            sunshineScheduledRecoveryCancellation = null;
            sunshineScheduledRecoveryTransaction = null;
            sunshineScheduledRecoveryDelay = TimeSpan.Zero;
            sunshineScheduledRecoveryHasNoSessionProof = false;
            sunshineScheduledRecoveryProofIsRetractable = false;
        }
    }

    private void ReconcileSunshineRecoveryAfterSessionStart(
        string expectedTransaction)
    {
        CancellationTokenSource? retractable = null;
        lock (sunshineLifecycleSync)
        {
            if (sunshineScheduledRecoveryProofIsRetractable &&
                string.Equals(
                    sunshineScheduledRecoveryTransaction,
                    expectedTransaction,
                    StringComparison.Ordinal))
            {
                retractable = sunshineScheduledRecoveryCancellation;
                sunshineScheduledRecoveryCancellation = null;
                sunshineScheduledRecoveryTransaction = null;
                sunshineScheduledRecoveryDelay = TimeSpan.Zero;
                sunshineScheduledRecoveryHasNoSessionProof = false;
                sunshineScheduledRecoveryProofIsRetractable = false;
            }
        }
        retractable?.Cancel();
        ScheduleSunshineRecovery(
            expectedTransaction,
            HostRecoveryActions.GetStreamBoundaryRecoveryDelay(
                expectedTransaction),
            "authenticated Vita stream lease expired",
            hasNoSessionProof: false,
            proofIsRetractable: false);
    }

    private bool TryRecoverAfterSunshineExit(
        string expectedTransaction,
        string reason,
        bool hasNoSessionProof,
        CancellationToken token)
    {
        if (token.IsCancellationRequested ||
            disposed ||
            Volatile.Read(ref suspendPending) != 0 ||
            !HostRecoveryActions.IsSunshineExitRecoveryAllowed() ||
            !string.Equals(
                HostRecoveryActions.CapturePendingTransactionMarker(),
                expectedTransaction,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (Interlocked.CompareExchange(
                ref actionRunning,
                DisplayActionRunning,
                0) != 0)
        {
            return false;
        }

        try
        {
            if (token.IsCancellationRequested ||
                disposed ||
                Volatile.Read(ref suspendPending) != 0)
            {
                return true;
            }
            return HostRecoveryActions.TryRecoverAfterSunshineExit(
                expectedTransaction,
                reason,
                hasNoSessionProof);
        }
        finally
        {
            try
            {
                if (Volatile.Read(ref suspendPending) != 0)
                {
                    HostRecoveryActions.PrepareDisplaysForSuspend(
                        SuspendDisplayLeaseBudget);
                }
            }
            finally
            {
                Interlocked.Exchange(ref actionRunning, 0);
            }
        }
    }

    private Process? FindSunshineProcess()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("sunshine");
        }
        catch (Exception error) when (
            error is InvalidOperationException or
                System.ComponentModel.Win32Exception or
                PlatformNotSupportedException)
        {
            return null;
        }

        Process? selected = null;
        foreach (var candidate in candidates)
        {
            try
            {
                if (selected is not null || candidate.HasExited)
                {
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(sunshineExecutablePath))
                {
                    var imagePath = candidate.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(imagePath) ||
                        !string.Equals(
                            Path.GetFullPath(imagePath),
                            sunshineExecutablePath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }
                selected = candidate;
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException or
                    IOException or
                    UnauthorizedAccessException)
            {
                // Keep polling while absent rather than binding the recovery
                // agent to a process whose image Windows would not identify.
            }
            finally
            {
                if (!ReferenceEquals(candidate, selected))
                {
                    candidate.Dispose();
                }
            }
        }
        return selected;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pendingAudioRecoveryCancellation.Cancel();
        sunshineWatchCancellation.Cancel();
        StopPendingAudioRecoveryWatcher();
        StopSunshineLifecycleWatchers();
        StopResumeObservation();
        if (Handle != IntPtr.Zero)
        {
            foreach (var hotkeyId in registeredHotkeys)
            {
                UnregisterHotKey(Handle, hotkeyId);
            }
            registeredHotkeys.Clear();
            DestroyHandle();
        }
        foreach (var readiness in modeReadinessEvents)
        {
            readiness.Reset();
            readiness.Dispose();
        }
        modeReadinessEvents.Clear();
        StopSunshineWatcher();
    }

    private void StopPendingAudioRecoveryWatcher()
    {
        FileSystemWatcher? watcher;
        Task worker;
        lock (pendingAudioRecoverySync)
        {
            watcher = pendingAudioRecoveryWatcher;
            pendingAudioRecoveryWatcher = null;
            worker = pendingAudioRecoveryTask;
            pendingAudioRecoveryRequested = false;
        }
        watcher?.Dispose();

        try
        {
            worker.Wait(PendingAudioRecoveryDisposeWait);
        }
        catch (AggregateException error)
        {
            // Observe cancellation and any unexpected worker fault without
            // making application-context disposal throw on the UI thread.
            _ = error;
        }

        if (worker.IsCompleted)
        {
            _ = worker.Exception;
            pendingAudioRecoveryCancellation.Dispose();
            return;
        }

        _ = worker.ContinueWith(
            (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            pendingAudioRecoveryCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void StopSunshineLifecycleWatchers()
    {
        CancelScheduledSunshineRecovery();
        DisarmSunshineLogWatcher();
        var recoveryWatcher = Interlocked.Exchange(
            ref sunshineRecoveryWatcher,
            null);
        recoveryWatcher?.Dispose();
    }

    private void StopSunshineWatcher()
    {
        try
        {
            sunshineWatchTask.Wait(SunshineWatcherDisposeWait);
        }
        catch (AggregateException error) when (
            error.InnerExceptions.All(inner =>
                inner is OperationCanceledException))
        {
            // Cancellation is the normal shutdown path.
        }

        if (sunshineWatchTask.IsCompleted)
        {
            sunshineWatchCancellation.Dispose();
            return;
        }

        _ = sunshineWatchTask.ContinueWith(
            (completed, state) =>
            {
                _ = completed.Exception;
                ((CancellationTokenSource)state!).Dispose();
            },
            sunshineWatchCancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private bool IsResumeObservationOpen()
    {
        lock (resumeInspectionSync)
        {
            return !disposed &&
                   resumeRecoveryRuntime is { } runtime &&
                   runtime.Elapsed < MaximumResumeRecoveryRuntime &&
                   DateTimeOffset.UtcNow < resumeObservationEndsAt;
        }
    }

    private bool ScheduleResumeInspection(
        string trigger,
        bool beginObservation,
        CancellationTokenSource? expectedCurrent = null)
    {
        CancellationTokenSource? previous;
        CancellationTokenSource current;
        DateTimeOffset observationStartedAt;
        string? pendingTransactionAtWake;
        var currentPendingTransaction = beginObservation
            ? HostRecoveryActions.CapturePendingTransactionMarker()
            : null;
        lock (resumeInspectionSync)
        {
            if (disposed) return false;
            if (expectedCurrent is not null &&
                !ReferenceEquals(
                    resumeInspectionCancellation,
                    expectedCurrent))
            {
                return false;
            }
            var now = DateTimeOffset.UtcNow;
            if (beginObservation)
            {
                resumeObservationStartedAt = now;
                resumeObservationEndsAt = now + ResumeObservationWindow;
                resumeRecoveryRuntime = Stopwatch.StartNew();
                resumePendingTransactionAtWake = currentPendingTransaction;
            }
            else
            {
                var runtime = resumeRecoveryRuntime;
                if (runtime is null ||
                    runtime.Elapsed >= MaximumResumeRecoveryRuntime)
                {
                    return false;
                }
                if (now >= resumeObservationEndsAt)
                {
                    if (Volatile.Read(ref currentSuspendIntent) is null)
                    {
                        return false;
                    }
                    // A live durable suspend token must not become a permanent
                    // block merely because another process held session.lock
                    // past the ordinary topology-observation window. Extend
                    // individual attempts only inside the absolute recovery
                    // budget. If that budget expires, the durable token stays
                    // in place and blocks display mutation without continuing
                    // background churn.
                    var remaining =
                        MaximumResumeRecoveryRuntime - runtime.Elapsed;
                    resumeObservationEndsAt = now +
                        (remaining < ResumeObservationWindow
                            ? remaining
                            : ResumeObservationWindow);
                }
            }

            previous = resumeInspectionCancellation;
            current = new CancellationTokenSource();
            resumeInspectionCancellation = current;
            observationStartedAt = resumeObservationStartedAt;
            pendingTransactionAtWake = resumePendingTransactionAtWake;
        }

        TryCancel(previous);
        _ = InspectResumeTopologyAsync(
            trigger,
            observationStartedAt,
            pendingTransactionAtWake,
            beginObservation ? ResumeInitialDelay : ResumeFollowupDelay,
            current);
        return true;
    }

    private async Task InspectResumeTopologyAsync(
        string trigger,
        DateTimeOffset observationStartedAt,
        string? pendingTransactionAtWake,
        TimeSpan initialDelay,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(initialDelay, token).ConfigureAwait(false);
            if (Volatile.Read(ref currentSuspendIntent) is not null)
            {
                // A durable PBT_APMSUSPEND token is itself sufficient reason
                // to enter the serialized physical-recovery path. Do not make
                // its cleanup depend on read-only topology enumeration being
                // available immediately after wake.
                await RecoverAfterResumeIfStillRequiredAsync(
                        trigger,
                        pendingTransactionAtWake,
                        new ResumeTopologySnapshot(
                            0,
                            0,
                            0,
                            0,
                            PhysicalDisplayModeRepairResult.Empty,
                            pendingTransactionAtWake,
                            ManagedVirtualPnpDisabled: false,
                            Signature: "suspend-fence-pending"),
                        hardDeadlineReached: false,
                        cancellation)
                    .ConfigureAwait(false);
                return;
            }
            ResumeTopologySnapshot? previous = null;
            var stableSamples = 0;
            Exception? lastInspectionError = null;
            var restoredPhysicalModes = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var modeRepairWarnings = new Dictionary<string,
                PhysicalDisplayModeRepairWarning>(StringComparer.Ordinal);

            for (var attempt = 1; attempt <= ResumeSampleAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentResumeInspection(cancellation)) return;
                ResumeTopologySnapshot snapshot;
                try
                {
                    snapshot = CaptureResumeTopology();
                    if (!IsCurrentResumeInspection(cancellation)) return;
                    foreach (var restored in
                             snapshot.ModeRepair.RestoredModes)
                    {
                        restoredPhysicalModes.Add(restored);
                    }
                    foreach (var warning in snapshot.ModeRepair.Warnings)
                    {
                        modeRepairWarnings[
                            $"{warning.Code}\u001f{warning.Display}\u001f{warning.Detail}"] =
                            warning;
                    }
                    snapshot = snapshot with
                    {
                        ModeRepair = new PhysicalDisplayModeRepairResult(
                            restoredPhysicalModes.ToArray(),
                            modeRepairWarnings.Values.ToArray()),
                    };
                    lastInspectionError = null;
                }
                catch (Exception error) when (
                    error is InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        IOException or
                        UnauthorizedAccessException)
                {
                    lastInspectionError = error;
                    previous = null;
                    stableSamples = 0;
                    if (!IsResumeObservationOpen())
                    {
                        break;
                    }
                    if (attempt < ResumeSampleAttempts)
                    {
                        await Task.Delay(ResumeSampleInterval, token).ConfigureAwait(false);
                    }
                    continue;
                }

                stableSamples = previous?.Signature == snapshot.Signature
                    ? stableSamples + 1
                    : 1;
                previous = snapshot;

                var observationAge = DateTimeOffset.UtcNow - observationStartedAt;
                var decision = ResumeTopologyClassifier.Classify(
                    snapshot.ActivePhysicalPaths,
                    snapshot.ActiveManagedVirtualPaths,
                    pendingTransactionAtWake,
                    snapshot.PendingTransactionMarker,
                    stableSamples,
                    observationAge >= MinimumRecoveryAge,
                    snapshot.ManagedVirtualPnpDisabled);
                if (decision is ResumeTopologyDecision.Healthy or
                    ResumeTopologyDecision.ReconcileIdle)
                {
                    // Finalize even a healthy observation under the shared
                    // display lease. This is where an unambiguous Windows
                    // fallback mode is repaired; routine samples stay
                    // strictly read-only and cannot race a session/uninstall.
                    await RecoverAfterResumeIfStillRequiredAsync(
                            trigger,
                            pendingTransactionAtWake,
                            snapshot,
                            hardDeadlineReached: false,
                            cancellation)
                        .ConfigureAwait(false);
                    return;
                }
                if (decision == ResumeTopologyDecision.Recover)
                {
                    await RecoverAfterResumeIfStillRequiredAsync(
                            trigger,
                            pendingTransactionAtWake,
                            snapshot,
                            hardDeadlineReached: false,
                            cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                if (!IsResumeObservationOpen())
                {
                    await RecoverAfterResumeIfStillRequiredAsync(
                            trigger,
                            pendingTransactionAtWake,
                            snapshot,
                            hardDeadlineReached: true,
                            cancellation)
                        .ConfigureAwait(false);
                    return;
                }

                if (attempt < ResumeSampleAttempts)
                {
                    await Task.Delay(ResumeSampleInterval, token).ConfigureAwait(false);
                }
            }

            if (lastInspectionError is not null)
            {
                if (!ScheduleResumeInspection(
                        "resume-enumeration-retry",
                        beginObservation: false,
                        expectedCurrent: cancellation) &&
                    IsCurrentResumeInspection(cancellation))
                {
                    HostRecoveryActions.RecordResumeDecision(
                        trigger,
                        false,
                        $"Windows display enumeration did not settle before the recovery deadline: {lastInspectionError.Message}");
                }
            }
            else if (previous is not null)
            {
                if (!ScheduleResumeInspection(
                        "resume-topology-retry",
                        beginObservation: false,
                        expectedCurrent: cancellation) &&
                    IsCurrentResumeInspection(cancellation))
                {
                    await RecoverAfterResumeIfStillRequiredAsync(
                            trigger,
                            pendingTransactionAtWake,
                            previous,
                            hardDeadlineReached: true,
                            cancellation)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // A newer resume/display event superseded this observation.
        }
        catch (Exception error)
        {
            HostRecoveryActions.RecordUnhandledFailure(
                $"resume-display-check:{trigger}",
                error);
        }
        finally
        {
            lock (resumeInspectionSync)
            {
                if (ReferenceEquals(resumeInspectionCancellation, cancellation))
                {
                    resumeInspectionCancellation = null;
                }
            }
            cancellation.Dispose();
        }
    }

    private async Task RecoverAfterResumeIfStillRequiredAsync(
        string trigger,
        string? pendingTransactionAtWake,
        ResumeTopologySnapshot observed,
        bool hardDeadlineReached,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        // Give the display stack one final quiet interval after the candidate
        // topology. This confirmation and the action gate close the common
        // races with late monitor enumeration and a manual rescue hotkey.
        await Task.Delay(ResumeFollowupDelay, token).ConfigureAwait(false);
        if (!IsCurrentResumeInspection(cancellation)) return;
        if (Interlocked.CompareExchange(
                ref actionRunning,
                DisplayActionRunning,
                0) != 0)
        {
            if (!ScheduleResumeInspection(
                    "resume-action-retry",
                    beginObservation: false,
                    expectedCurrent: cancellation) &&
                IsCurrentResumeInspection(cancellation))
            {
                HostRecoveryActions.RecordResumeDecision(
                    trigger,
                    false,
                    "Automatic recovery reached its deadline while another rescue action was still running.");
            }
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentResumeInspection(cancellation)) return;
            DisplayTransactionLease transaction;
            try
            {
                transaction = DisplayTransactionLock.Acquire();
            }
            catch (Exception error)
            {
                if (!hardDeadlineReached &&
                    ScheduleResumeInspection(
                        "resume-display-transaction-retry",
                        beginObservation: false,
                        expectedCurrent: cancellation))
                {
                    return;
                }

                if (StopResumeObservation(
                        cancelCurrentInspection: false,
                        expectedCurrent: cancellation))
                {
                    HostRecoveryActions.RecordResumeDecision(
                        trigger,
                        false,
                        "Automatic recovery did not change the display because " +
                        $"another display transaction was active: {error.Message}");
                }
                return;
            }
            using var heldTransaction = transaction;
            token.ThrowIfCancellationRequested();
            if (!IsCurrentResumeInspection(cancellation)) return;

            // Keep the durable suspend fence in place until this worker owns
            // the same machine-wide display transaction as session start and
            // mode changes. Even a session command which held the lease
            // across the entire sleep/resume boundary is forced back to a
            // verified physical-only topology before its exact token is
            // removed.
            if (Volatile.Read(ref currentSuspendIntent) is { } suspendIntent)
            {
                try
                {
                    var physical = UninstallManager
                        .RecoverPhysicalAndDiscardPendingTransactionLocked(
                            heldTransaction);
                    if (!DisplaySuspendIntentStore
                            .ClearOwnedAfterPhysicalResumeLocked(
                                heldTransaction,
                                suspendIntent))
                    {
                        throw new InvalidOperationException(
                            "The exact Windows suspend fence changed before physical-resume verification committed.");
                    }
                    Interlocked.CompareExchange(
                        ref currentSuspendIntent,
                        null,
                        suspendIntent);
                    if (StopResumeObservation(
                            cancelCurrentInspection: false,
                            expectedCurrent: cancellation))
                    {
                        HostRecoveryActions.RecordResumeDecision(
                            trigger,
                            true,
                            "Recovered and verified physical-only after Windows resume; " +
                            $"physical displays: {string.Join(", ", physical.PhysicalDisplays)}.");
                    }
                }
                catch (Exception error)
                {
                    if (!hardDeadlineReached &&
                        ScheduleResumeInspection(
                            "resume-suspend-fence-retry",
                            beginObservation: false,
                            expectedCurrent: cancellation))
                    {
                        return;
                    }
                    if (StopResumeObservation(
                            cancelCurrentInspection: false,
                            expectedCurrent: cancellation))
                    {
                        HostRecoveryActions.RecordResumeDecision(
                            trigger,
                            false,
                            "Windows resumed, but the physical-only suspend fence could not be completed. " +
                            "It remains active and blocks Vita display changes until agent startup recovery succeeds: " +
                            error.Message);
                    }
                }
                return;
            }

            // Confirm the observed topology without changing it. Only after
            // all generation/session checks pass may this lease repair a
            // persisted physical mode.
            var confirmed = CaptureResumeTopology();
            if (!IsCurrentResumeInspection(cancellation)) return;
            if (ResumeTopologyClassifier.SessionStartedAfterResume(
                    pendingTransactionAtWake,
                    confirmed.PendingTransactionMarker))
            {
                if (StopResumeObservation(
                        cancelCurrentInspection: false,
                        expectedCurrent: cancellation))
                {
                    HostRecoveryActions.RecordResumeDecision(
                        trigger,
                        true,
                        $"A Vita session became active while recovery was being considered; {confirmed.Summary}.");
                }
                return;
            }
            if (confirmed.Signature != observed.Signature &&
                !hardDeadlineReached)
            {
                if (ScheduleResumeInspection(
                        "resume-topology-changed",
                        beginObservation: false,
                        expectedCurrent: cancellation))
                {
                    return;
                }
                token.ThrowIfCancellationRequested();
                if (!IsCurrentResumeInspection(cancellation)) return;
                hardDeadlineReached = true;
            }
            var decision = ResumeTopologyClassifier.Classify(
                confirmed.ActivePhysicalPaths,
                confirmed.ActiveManagedVirtualPaths,
                pendingTransactionAtWake,
                confirmed.PendingTransactionMarker,
                ResumeTopologyClassifier.RecoverySamplesRequired,
                minimumRecoveryAgeReached: true,
                confirmed.ManagedVirtualPnpDisabled);
            if (decision == ResumeTopologyDecision.ReconcileIdle)
            {
                try
                {
                    var idle = ManagedVirtualDisplayRuntime
                        .ReconcileIdleLocked(
                            heldTransaction,
                            requireManagedDevice: false);
                    var audioPending = PendingAudioRecoveryRecordExists();
                    if (audioPending)
                    {
                        // This method still owns the display transaction. Arm
                        // the separate delayed worker, but never call Core
                        // Audio or wait for endpoint enumeration under it.
                        SchedulePendingAudioRecovery();
                    }
                    var audioDetail = audioPending
                        ? " Exact pre-stream audio recovery is pending in the asynchronous endpoint worker."
                        : " No pre-stream audio endpoint recovery is pending.";
                    if (StopResumeObservation(
                            cancelCurrentInspection: false,
                            expectedCurrent: cancellation))
                    {
                        HostRecoveryActions.RecordResumeDecision(
                            trigger,
                            true,
                            "Windows resumed to a physical-only topology, but the managed Vita VDD was still PnP-enabled; " +
                            $"reconciled and verified idle for {string.Join(", ", idle.PhysicalDisplays)}." +
                            audioDetail);
                    }
                }
                catch (Exception error)
                {
                    if (!hardDeadlineReached &&
                        ScheduleResumeInspection(
                            "resume-pnp-idle-retry",
                            beginObservation: false,
                            expectedCurrent: cancellation))
                    {
                        return;
                    }
                    if (StopResumeObservation(
                            cancelCurrentInspection: false,
                            expectedCurrent: cancellation))
                    {
                        HostRecoveryActions.RecordResumeDecision(
                            trigger,
                            false,
                            "Windows resumed with the managed Vita VDD still PnP-enabled, and idle reconciliation failed: " +
                            error.Message);
                    }
                }
                return;
            }
            if (decision != ResumeTopologyDecision.Recover)
            {
                token.ThrowIfCancellationRequested();
                if (!IsCurrentResumeInspection(cancellation)) return;
                var repaired = CaptureResumeTopology(
                    repairPhysicalModes: true);
                token.ThrowIfCancellationRequested();
                if (StopResumeObservation(
                        cancelCurrentInspection: false,
                        expectedCurrent: cancellation))
                {
                    HostRecoveryActions.RecordResumeDecision(
                        trigger,
                        true,
                        $"The topology no longer requires recovery; {repaired.Summary}.");
                }
                return;
            }

            token.ThrowIfCancellationRequested();
            if (!IsCurrentResumeInspection(cancellation)) return;
            var recovery = HostRecoveryActions
                .RecoverDisplayAndStreamingHostLocked(
                    trigger,
                    heldTransaction,
                    suspendIsPending: () =>
                        Volatile.Read(ref suspendPending) != 0);
            if (recovery.Success)
            {
                StopResumeObservation(
                    cancelCurrentInspection: false,
                    expectedCurrent: cancellation);
            }
            else if (!ScheduleResumeInspection(
                         "resume-recovery-retry",
                         beginObservation: false,
                         expectedCurrent: cancellation) &&
                     IsCurrentResumeInspection(cancellation))
            {
                if (StopResumeObservation(
                        cancelCurrentInspection: false,
                        expectedCurrent: cancellation))
                {
                    HostRecoveryActions.RecordResumeDecision(
                        trigger,
                        false,
                        "Automatic physical-display recovery failed and the bounded resume retry window has ended.");
                }
            }
        }
        finally
        {
            try
            {
                if (Volatile.Read(ref suspendPending) != 0)
                {
                    HostRecoveryActions.PrepareDisplaysForSuspend(
                        SuspendDisplayLeaseBudget);
                }
            }
            finally
            {
                Interlocked.Exchange(ref actionRunning, 0);
            }
        }
    }

    private static ResumeTopologySnapshot CaptureResumeTopology(
        bool repairPhysicalModes = false)
    {
        var topology = new DisplayTopologyService();
        var modeRepair = repairPhysicalModes
            ? topology.RestorePersistedPhysicalDisplayModes()
            : PhysicalDisplayModeRepairResult.Empty;
        var active = topology.ListDisplays()
            .Where(display => display.IsActive)
            .ToArray();
        var activePhysical = active.Count(display =>
            display.IsAvailable &&
            !DisplayTopologyService.IsLikelyVirtualDisplay(display));
        var activeManagedVirtual = active.Count(
            DisplayTopologyService.IsManagedVirtualDisplay);
        var activeOtherVirtual = active.Count(display =>
            DisplayTopologyService.IsLikelyVirtualDisplay(display) &&
            !DisplayTopologyService.IsManagedVirtualDisplay(display));
        var pendingTransactionMarker = HostRecoveryActions
            .CapturePendingTransactionMarker();
        var managedVirtualPnpDisabled = HostRecoveryActions
            .AreManagedVirtualDisplayDevicesDisabledForResume();
        var pathSignature = string.Join(
            "|",
            active
                .Select(display =>
                    $"{display.DevicePath}\u001f{display.FriendlyName}\u001f{display.OutputTechnology}\u001f" +
                    $"{display.Width}x{display.Height}@{display.RefreshRate}")
                .OrderBy(identity => identity, StringComparer.OrdinalIgnoreCase));
        return new ResumeTopologySnapshot(
            active.Length,
            activePhysical,
            activeManagedVirtual,
            activeOtherVirtual,
            modeRepair,
            pendingTransactionMarker,
            managedVirtualPnpDisabled,
            $"{pendingTransactionMarker ?? "<none>"}:{managedVirtualPnpDisabled}:{pathSignature}");
    }

    private bool IsCurrentResumeInspection(
        CancellationTokenSource expected)
    {
        lock (resumeInspectionSync)
        {
            return !disposed &&
                   ReferenceEquals(
                       resumeInspectionCancellation,
                       expected);
        }
    }

    private bool StopResumeObservation(
        bool cancelCurrentInspection = true,
        CancellationTokenSource? expectedCurrent = null)
    {
        CancellationTokenSource? cancellation;
        lock (resumeInspectionSync)
        {
            if (expectedCurrent is not null &&
                !ReferenceEquals(
                    resumeInspectionCancellation,
                    expectedCurrent))
            {
                return false;
            }
            resumeObservationEndsAt = DateTimeOffset.MinValue;
            resumeRecoveryRuntime = null;
            resumePendingTransactionAtWake = null;
            cancellation = resumeInspectionCancellation;
            if (cancelCurrentInspection)
            {
                resumeInspectionCancellation = null;
            }
        }
        if (cancelCurrentInspection)
        {
            TryCancel(cancellation);
        }
        return true;
    }

    private static void TryCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded inspection completed between selection and cancellation.
        }
    }

    private sealed record ResumeTopologySnapshot(
        int ActivePaths,
        int ActivePhysicalPaths,
        int ActiveManagedVirtualPaths,
        int ActiveOtherVirtualPaths,
        PhysicalDisplayModeRepairResult ModeRepair,
        string? PendingTransactionMarker,
        bool ManagedVirtualPnpDisabled,
        string Signature)
    {
        internal string Summary =>
            $"active paths={ActivePaths}, physical={ActivePhysicalPaths}, " +
            $"managed Vita VDD={ActiveManagedVirtualPaths}, other virtual={ActiveOtherVirtualPaths}, " +
            $"managed Vita VDD PnP disabled={ManagedVirtualPnpDisabled.ToString().ToLowerInvariant()}, " +
            $"restored physical modes={ModeRepair.RestoredModes.Count}, " +
            $"mode-repair warnings={ModeRepair.Warnings.Count}" +
            (ModeRepair.Warnings.Count == 0
                ? string.Empty
                : $" [{string.Join(", ", ModeRepair.Warnings.Select(warning => warning.Code).Distinct())}]") + ", " +
            $"session pending={(PendingTransactionMarker is not null).ToString().ToLowerInvariant()}";
    }

    internal static VitaDisplayMode? ModeForHotkeyId(int hotkeyId) => hotkeyId switch
    {
        Mode960x540HotkeyId => new VitaDisplayMode(960, 540, 60),
        Mode960x544HotkeyId => VitaDisplayModes.Native,
        Mode1280x720HotkeyId => new VitaDisplayMode(1280, 720, 60),
        _ => null,
    };

    private void RegisterRequiredHotkey(int hotkeyId, uint modifiers, uint virtualKey, string action)
    {
        if (!RegisterHotKey(Handle, hotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"The mandatory Vita Moonlight {action} hotkey could not be registered (Windows error {error}).");
        }
        registeredHotkeys.Add(hotkeyId);
    }

    private void RegisterOptionalModeHotkey(
        int hotkeyId,
        uint modifiers,
        uint virtualKey,
        VitaDisplayMode mode)
    {
        EventWaitHandle readiness;
        try
        {
            readiness = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                HostRecoveryAgentManager.ModeHotkeyReadyEventName(mode));
            readiness.Reset();
            modeReadinessEvents.Add(readiness);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            return;
        }

        if (!RegisterHotKey(Handle, hotkeyId, modifiers, virtualKey))
        {
            return;
        }
        registeredHotkeys.Add(hotkeyId);
        readiness.Set();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

internal static class HostRecoveryActions
{
    private const long MaximumRescueLogBytes = 512 * 1024;
    private const int MaximumUnfencedSuspendFallbackAttempts = 128;
    private static readonly TimeSpan SunshineExitDisplayLeaseWait =
        TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MaximumUnfencedSuspendFallbackRuntime =
        TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MaximumUnfencedPostResumeRecoveryRuntime =
        TimeSpan.FromSeconds(30);
    private static int unfencedSuspendFallbackRunning;

    internal static string? CapturePendingTransactionMarker()
    {
        if (!File.Exists(HostStatePaths.RecoveryFile)) return null;
        try
        {
            var recovery = new DisplayTopologyService().LoadRecovery();
            return $"{recovery.CapturedAt.UtcDateTime.Ticks}:" +
                   $"{recovery.RequestedWidth}x{recovery.RequestedHeight}x{recovery.RequestedFps}";
        }
        catch
        {
            // A malformed record still identifies interrupted work. File
            // metadata lets a later valid/new transaction be distinguished
            // without trusting or applying the malformed content.
            try
            {
                var information = new FileInfo(HostStatePaths.RecoveryFile);
                return $"unreadable:{information.LastWriteTimeUtc.Ticks}:{information.Length}";
            }
            catch
            {
                return "present-unreadable";
            }
        }
    }

    internal static DateTimeOffset? CaptureDurableRecoveryCapturedAtUtc()
    {
        if (!File.Exists(HostStatePaths.RecoveryFile)) return null;
        try
        {
            return new DisplayTopologyService()
                .LoadRecovery()
                .CapturedAt
                .ToUniversalTime();
        }
        catch
        {
            // A present but unreadable recovery record must never be treated
            // as idle. UnixEpoch cannot match a valid newly captured handoff,
            // so the lease classifier safely reports orphan/mismatch.
            return DateTimeOffset.UnixEpoch;
        }
    }

    internal static TimeSpan GetStreamBoundaryRecoveryDelay(
        string expectedTransaction,
        DateTimeOffset? nowUtc = null)
    {
        if (!string.Equals(
                CapturePendingTransactionMarker(),
                expectedTransaction,
                StringComparison.Ordinal))
        {
            return TimeSpan.Zero;
        }
        var now = (nowUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var durableCapturedAt = CaptureDurableRecoveryCapturedAtUtc();
        try
        {
            var assessment = StreamBoundaryLeaseJournal.Assess(
                durableCapturedAt,
                now);
            return GetStreamBoundaryRecoveryDelayForAssessment(
                assessment,
                now);
        }
        catch (Exception error) when (
            StreamBoundaryBridgeServer.IsOperationalRequestFailure(error))
        {
            // If the protected lease cannot be read, retain the display for
            // at most the full Prepared bound measured from the durable
            // recovery capture. This avoids both immediate teardown of a
            // fresh launch and indefinite ownership by a damaged journal.
            if (durableCapturedAt is not { } captured ||
                captured == DateTimeOffset.UnixEpoch)
            {
                return TimeSpan.Zero;
            }
            var remaining = captured
                .Add(StreamBoundaryLeaseJournal.PreparedLifetime) - now;
            return remaining > TimeSpan.Zero
                ? remaining
                : TimeSpan.Zero;
        }
    }

    internal static TimeSpan GetStreamBoundaryRecoveryDelayForAssessment(
        StreamBoundaryLeaseAssessment assessment,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var now = nowUtc.ToUniversalTime();
        if (!assessment.AuthorizesActiveHandoff ||
            assessment.Lease is not { } lease)
        {
            return TimeSpan.Zero;
        }
        var remaining = lease.LeaseExpiresAtUtc - now;
        return remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.Zero;
    }

    internal static bool ShouldDeferRecoveryForLease(
        StreamBoundaryLeaseAssessment assessment,
        bool hasNoSessionProof)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        return assessment.AuthorizesActiveHandoff &&
            !hasNoSessionProof;
    }

    internal static void DiscardStaleStreamBoundaryLease()
    {
        try
        {
            var assessment = StreamBoundaryLeaseJournal.Assess(null);
            if (assessment.Disposition ==
                    StreamBoundaryLeaseDisposition.StaleLease)
            {
                _ = StreamBoundaryLeaseJournal.RemoveAssessed(assessment);
            }
        }
        catch (Exception error) when (
            StreamBoundaryBridgeServer.IsOperationalRequestFailure(error))
        {
            RecordSunshineExitDecision(
                false,
                $"The display is idle, but its stale protected stream lease could not be inspected: {error.Message}");
        }
    }

    // Keep resume classification behind one ownership boundary so a shared or
    // ambiguous MTT device can never make Vita idle look healthy.
    internal static bool AreManagedVirtualDisplayDevicesDisabledForResume() =>
        ManagedVddOwnershipJournal
            .RequireOwnedPresentDevices(required: false)
            .All(device => !device.Enabled);

    internal static bool ShouldRetrySunshineRecovery(
        bool recoverySucceeded,
        string expectedTransaction,
        string? currentTransaction) =>
        !recoverySucceeded &&
        string.Equals(
            expectedTransaction,
            currentTransaction,
            StringComparison.Ordinal);

    internal static bool IsSunshineExitRecoveryAllowed()
    {
        try
        {
            return BackendLifecycleManager.IsEnabled &&
                   !InstallerMaintenanceFence.IsPresent &&
                   !BackendLifecycleStateStore.IsUninstallInProgress();
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryRecoverAfterSunshineExit(
        string expectedTransaction,
        string reason,
        bool hasNoSessionProof = false)
    {
        if (!IsSunshineExitRecoveryAllowed() ||
            !string.Equals(
                CapturePendingTransactionMarker(),
                expectedTransaction,
                StringComparison.Ordinal))
        {
            return true;
        }
        if (!hasNoSessionProof &&
            GetStreamBoundaryRecoveryDelay(expectedTransaction) >
            TimeSpan.Zero)
        {
            return false;
        }

        DisplayTransactionLease transaction;
        try
        {
            transaction = DisplayTransactionLock.AcquireWithin(
                SunshineExitDisplayLeaseWait);
        }
        catch (TimeoutException)
        {
            return false;
        }

        using (transaction)
        {
            if (!IsSunshineExitRecoveryAllowed() ||
                !string.Equals(
                    CapturePendingTransactionMarker(),
                    expectedTransaction,
                    StringComparison.Ordinal))
            {
                return true;
            }
            StreamBoundaryLeaseAssessment? leaseAssessment = null;
            try
            {
                leaseAssessment = StreamBoundaryLeaseJournal.Assess(
                    CaptureDurableRecoveryCapturedAtUtc());
                if (leaseAssessment.AuthorizesActiveHandoff)
                {
                    if (ShouldDeferRecoveryForLease(
                            leaseAssessment,
                            hasNoSessionProof) ||
                        !StreamBoundaryLeaseJournal
                            .RemoveAssessedForProvenNoSession(
                                leaseAssessment))
                    {
                        // A concurrent heartbeat revision wins. Re-enter the
                        // bounded worker and reassess under this same exact
                        // process/global-zero proof before restoring.
                        return false;
                    }
                    leaseAssessment = StreamBoundaryLeaseJournal.Assess(
                        CaptureDurableRecoveryCapturedAtUtc());
                }
            }
            catch (Exception error) when (
                StreamBoundaryBridgeServer.IsOperationalRequestFailure(error))
            {
                RecordSunshineExitDecision(
                    false,
                    $"{reason}; the protected Vita stream lease was unreadable after its bounded launch lifetime: {error.Message}. Continuing exact recovery from the durable display record.");
            }
            var recoverySucceeded = false;
            try
            {
                var recovery = UninstallManager
                    .RecoverPhysicalAndDiscardPendingTransactionLocked(
                        transaction);
                RecordSunshineExitDecision(
                    true,
                    $"{reason}; recovered and verified the physical-only, PnP-disabled idle state for {string.Join(", ", recovery.PhysicalDisplays)}.");
                recoverySucceeded = true;
                if (leaseAssessment is { CanDiscardLeaseAfterRecovery: true })
                {
                    try
                    {
                        _ = StreamBoundaryLeaseJournal.RemoveAssessed(
                            leaseAssessment);
                    }
                    catch (Exception error) when (
                        StreamBoundaryBridgeServer
                            .IsOperationalRequestFailure(error))
                    {
                        RecordSunshineExitDecision(
                            false,
                            $"{reason}; the display was restored, but the exact expired stream lease could not be discarded: {error.Message}");
                    }
                }
            }
            catch (Exception error)
            {
                RecordSunshineExitDecision(
                    false,
                    $"{reason}; automatic physical/PnP idle recovery failed: {error.Message}");
            }
            return !ShouldRetrySunshineRecovery(
                recoverySucceeded,
                expectedTransaction,
                CapturePendingTransactionMarker());
        }
    }
    internal static void RecoverStaleSuspendIntentAtAgentStartup()
    {
        if (!File.Exists(DisplaySuspendIntentStore.IntentFile)) return;
        try
        {
            var recovered =
                DisplaySuspendIntentStore.TryRecoverStaleAtAgentStartup(
                    TimeSpan.FromSeconds(2),
                    out var message);
            Record(
                "startup-suspend-recovery",
                true,
                recovered
                    ? message
                    : $"No stale suspend fence was cleared: {message}");
        }
        catch (Exception error)
        {
            Record(
                "startup-suspend-recovery",
                false,
                "Could not recover an interrupted Windows suspend fence: " +
                error.Message);
        }
    }

    internal static void StartUnfencedSuspendRecoveryFallback(
        Func<bool> suspendIsPending)
    {
        if (Interlocked.CompareExchange(
                ref unfencedSuspendFallbackRunning,
                1,
                0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            Exception? lastError = null;
            var fallbackRuntime = Stopwatch.StartNew();
            Stopwatch? postResumeRecoveryRuntime = null;
            var attempts = 0;
            try
            {
                while (true)
                {
                    attempts++;
                    var stillSuspending = suspendIsPending();
                    if (!stillSuspending)
                    {
                        postResumeRecoveryRuntime ??= Stopwatch.StartNew();
                    }

                    try
                    {
                        using var transaction =
                            DisplayTransactionLock.AcquireWithin(
                                TimeSpan.FromMilliseconds(500));
                        var physical = UninstallManager
                            .RecoverPhysicalAndDiscardPendingTransactionLocked(
                                transaction);
                        lastError = null;
                        if (!suspendIsPending())
                        {
                            Record(
                                "unfenced-suspend-recovery",
                                true,
                                "Durable suspend-fence publication failed, but the fallback forced and verified physical-only after resume; " +
                                $"physical displays: {string.Join(", ", physical.PhysicalDisplays)}.");
                            return;
                        }
                    }
                    catch (Exception error)
                    {
                        lastError = error;
                    }

                    if (postResumeRecoveryRuntime is { } resumed &&
                        resumed.Elapsed >=
                            MaximumUnfencedPostResumeRecoveryRuntime)
                    {
                        Record(
                            "unfenced-suspend-recovery",
                            false,
                            "Durable suspend-fence publication failed and the fallback could not verify physical-only within 30 seconds after resume: " +
                            (lastError?.Message ?? "unknown display recovery error"));
                        return;
                    }
                    if (attempts >= MaximumUnfencedSuspendFallbackAttempts ||
                        fallbackRuntime.Elapsed >=
                            MaximumUnfencedSuspendFallbackRuntime)
                    {
                        var finalPhysicalVerificationSucceeded =
                            lastError is null;
                        Record(
                            "unfenced-suspend-recovery-circuit-breaker",
                            false,
                            finalPhysicalVerificationSucceeded
                                ? "The Windows resume flag did not clear within the bounded fallback window. " +
                                  "The last physical-only verification succeeded, so automatic retries were stopped to prevent display churn."
                                : "The Windows resume flag did not clear within the bounded fallback window, and physical-only verification still failed. " +
                                  "Automatic retries were stopped to prevent display churn; use the display recovery hotkey after Windows is fully awake. " +
                                  $"Last response: {lastError!.Message}");
                        return;
                    }
                    Thread.Sleep(250);
                }
            }
            finally
            {
                Interlocked.Exchange(
                    ref unfencedSuspendFallbackRunning,
                    0);
            }
        });
    }

    internal static HostRescueStatus ChangeVirtualDisplayMode(VitaDisplayMode mode)
    {
        try
        {
            var result = new SessionManager().ChangeMode(mode.Width, mode.Height, mode.Fps);
            return Record(
                $"display-mode-{mode.Width}x{mode.Height}",
                true,
                $"Changed {result.DisplayName} to {result.Mode}.");
        }
        catch (Exception error)
        {
            return Record($"display-mode-{mode.Width}x{mode.Height}", false, error.Message);
        }
    }

    internal static HostRescueStatus PrepareDisplaysForSuspend(
        TimeSpan? displayLeaseTimeout = null)
    {
        try
        {
            using var transaction = displayLeaseTimeout is { } timeout
                ? DisplayTransactionLock.AcquireWithin(timeout)
                : DisplayTransactionLock.Acquire();
            return PrepareDisplaysForSuspendLocked(transaction);
        }
        catch (Exception error)
        {
            return Record(
                "power-suspend-display-prepare",
                false,
                $"Could not acquire the display recovery transaction: {error.Message}");
        }
    }

    private static HostRescueStatus PrepareDisplaysForSuspendLocked(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        var hadPendingTransaction = File.Exists(HostStatePaths.RecoveryFile);
        var restoredTransaction = false;
        Exception? transactionFailure = null;
        try
        {
            restoredTransaction = new SessionManager()
                .RestoreIfPendingLocked(
                    transaction,
                    expectedCapturedAt: null,
                    waitForAudioEndpoint: false,
                    deferAudioEndpointRestore: true);
        }
        catch (Exception error)
        {
            // Do not let an unreadable record or a briefly busy session lock
            // leave the managed VDD as the sleep topology. Preserve the
            // record, force a visible physical path below, and report that
            // exact partial failure once.
            transactionFailure = error;
        }

        try
        {
            var topology = new DisplayTopologyService();
            // "Safe for suspend" always means the complete idle invariant:
            // physical-only topology and the exact managed Vita PnP node
            // disabled. If restoring an unreadable/failed record did not
            // clear it, this central idle path intentionally leaves that
            // record in place for later diagnosis/retry; suspend must never
            // fall back to merely deactivating the VDD topology path.
            var idleState = ManagedVirtualDisplayRuntime.ReconcileIdleLocked(
                transaction,
                requireManagedDevice: false);
            var physicalDisplays = idleState.PhysicalDisplays;
            UninstallManager.VerifyPhysicalOnlyTopology(topology);
            var work = new List<string>();
            if (restoredTransaction)
            {
                work.Add("restored the pending Vita display transaction");
            }
            else if (hadPendingTransaction && transactionFailure is not null)
            {
                work.Add($"could not restore the pending Vita display transaction: {transactionFailure.Message}");
            }
            work.Add(
                "reconciled the physical-only, PnP-disabled Vita display idle state");
            work.Add($"physical displays: {string.Join(", ", physicalDisplays)}");
            return Record(
                "power-suspend-display-prepare",
                transactionFailure is null,
                string.Join("; ", work));
        }
        catch (Exception error)
        {
            var transactionDetail = transactionFailure is null
                ? string.Empty
                : $" Pending transaction restoration also failed: {transactionFailure.Message}.";
            return Record(
                "power-suspend-display-prepare",
                false,
                $"Physical display preparation failed: {error.Message}.{transactionDetail}");
        }
    }

    internal static HostRescueStatus RecoverDisplayAndStreamingHost(
        Func<bool>? suspendIsPending = null) =>
        RecoverDisplayAndStreamingHost(
            trigger: null,
            suspendIsPending);

    internal static HostRescueStatus RecoverDisplayAndStreamingHost(
        string? trigger,
        Func<bool>? suspendIsPending = null)
    {
        try
        {
            using var transaction = DisplayTransactionLock.Acquire();
            return RecoverDisplayAndStreamingHostLocked(
                trigger,
                transaction,
                suspendIsPending);
        }
        catch (Exception error)
        {
            return Record(
                "recover-display-host",
                false,
                $"Could not acquire the display recovery transaction: {error.Message}");
        }
    }

    internal static HostRescueStatus RecoverDisplayAndStreamingHostLocked(
        string? trigger,
        DisplayTransactionLease transaction,
        Func<bool>? suspendIsPending = null)
    {
        transaction.RequireActive();
        var failures = new List<string>();
        var completed = new List<string>();
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            completed.Add($"triggered by {trigger}");
        }
        if (!BackendLifecycleManager.IsEnabled)
        {
            try
            {
                var recovery =
                    UninstallManager
                        .RecoverPhysicalAndDiscardPendingTransactionLocked(
                            transaction);
                completed.Add(
                    $"activated physical display " +
                    $"{string.Join(", ", recovery.PhysicalDisplays)}");
                if (recovery.ClearedSavedTransaction)
                {
                    completed.Add("discarded the pending display transaction");
                }
                completed.Add(
                    "Vita host features are paused; left shared Sunshine unchanged and kept the managed virtual display inactive");
            }
            catch (Exception error)
            {
                failures.Add($"physical display recovery: {error.Message}");
            }
            return Record(
                "recover-display-host",
                failures.Count == 0,
                string.Join("; ", completed.Concat(failures)));
        }

        var sunshineServiceName = StreamingHostLocator.FindSunshineServiceName();
        var sunshineState = WindowsServiceState.NotInstalled;
        try
        {
            sunshineState =
                WindowsServiceManager.GetState(sunshineServiceName);
        }
        catch (Exception error)
        {
            failures.Add($"inspect Sunshine service: {error.Message}");
        }
        var restartSunshine =
            sunshineState == WindowsServiceState.Running;
        var sunshineStopped = false;
        var physicalRecoverySucceeded = false;
        var idleStateVerifiedForSunshineRestart = false;
        var vddOnlyRecoveryBridgeUsed = false;

        try
        {
            var recovery =
                UninstallManager
                    .RecoverPhysicalAndDiscardPendingTransactionLocked(
                        transaction);
            completed.Add(
                $"activated physical display " +
                $"{string.Join(", ", recovery.PhysicalDisplays)}");
            if (recovery.ClearedSavedTransaction)
            {
                completed.Add(
                    "discarded the pending display transaction");
            }
            physicalRecoverySucceeded = true;
            idleStateVerifiedForSunshineRestart = true;
        }
        catch (PhysicalDisplayUnavailableException initialRecoveryError)
        {
            // A prior broken install can leave QueryDisplayConfig with only
            // the managed VDD and no working task. Stop Sunshine before the
            // narrowly gated device rescan/exact-VDD restart so its display
            // helper cannot race physical restoration. It is restarted below
            // only if it was running on entry.
            var bootstrapAllowed = suspendIsPending?.Invoke() != true;
            if (!bootstrapAllowed)
            {
                failures.Add(
                    "physical display recovery: Windows suspend is pending; skipped the managed-VDD recovery bridge");
            }
            if (restartSunshine)
            {
                try
                {
                    if (bootstrapAllowed)
                    {
                        WindowsServiceManager.Stop(
                            sunshineServiceName,
                            "Sunshine");
                        sunshineStopped = true;
                        completed.Add(
                            "stopped Sunshine before VDD-only recovery");
                    }
                }
                catch (Exception error)
                {
                    bootstrapAllowed = false;
                    failures.Add(
                        $"stop Sunshine before VDD-only recovery: {error.Message}");
                }
            }

            if (bootstrapAllowed)
            {
                try
                {
                    var recovery = UninstallManager
                        .RecoverPhysicalAndDiscardPendingTransactionForEmergencyLocked(
                            transaction);
                    completed.Add(
                        $"activated physical display " +
                        $"{string.Join(", ", recovery.PhysicalDisplays)} after managed-VDD recovery");
                    if (recovery.ClearedSavedTransaction)
                    {
                        completed.Add(
                            "discarded the pending display transaction");
                    }
                    physicalRecoverySucceeded = true;
                    idleStateVerifiedForSunshineRestart = true;
                    vddOnlyRecoveryBridgeUsed = true;
                }
                catch (Exception error)
                {
                    failures.Add(
                        $"physical display recovery: {error.Message} " +
                        $"Initial response: {initialRecoveryError.Message}");
                }
            }
        }
        catch (Exception error)
        {
            failures.Add($"physical display recovery: {error.Message}");
        }

        // Make the monitor safe before beginning service or PnP maintenance.
        // If Windows announces suspend while another rescue was in flight,
        // the in-flight action now becomes the suspend preparation and exits
        // without starting a slow driver restart.
        if (suspendIsPending?.Invoke() == true)
        {
            completed.Add(
                "Windows suspend is pending; kept the recovered physical topology and skipped Sunshine/driver maintenance");
            return Record(
                "recover-display-host",
                failures.Count == 0,
                string.Join("; ", completed.Concat(failures)));
        }

        if (restartSunshine && !sunshineStopped)
        {
            try
            {
                WindowsServiceManager.Stop(sunshineServiceName, "Sunshine");
                sunshineStopped = true;
                completed.Add("stopped Sunshine");
            }
            catch (Exception error)
            {
                failures.Add($"stop Sunshine: {error.Message}");
            }
        }

        HostSettings settings;
        try
        {
            settings = HostSettings.Load();
        }
        catch (Exception error)
        {
            settings = HostSettings.Default;
            failures.Add(
                $"host settings were unreadable; used safe defaults: " +
                $"{error.Message}");
        }

        var sunshine =
            sunshineState != WindowsServiceState.NotInstalled ||
            settings.HostMode.Equals(
                "sunshine",
                StringComparison.OrdinalIgnoreCase);
        var skipDriverMaintenance = suspendIsPending?.Invoke() == true;
        if (skipDriverMaintenance)
        {
            completed.Add(
                "Windows suspend became pending; skipped virtual-display reload");
        }
        if (!skipDriverMaintenance &&
            physicalRecoverySucceeded &&
            !vddOnlyRecoveryBridgeUsed &&
            (!restartSunshine || sunshineStopped) &&
            sunshine &&
            DisplayWizardAdapter.IsDriverInstalled())
        {
            try
            {
                idleStateVerifiedForSunshineRestart = false;
                _ = new SessionManager()
                    .PrimeNativeModeForDriverMaintenanceOnlyLocked(
                        transaction);
                var topology = new DisplayTopologyService();
                if (!topology.TryCaptureExactPhysicalOnlySnapshot(
                        out var physicalSnapshot) ||
                    physicalSnapshot is null)
                {
                    throw new InvalidOperationException(
                        "Driver reset completed without a complete physical-only display snapshot.");
                }
                idleStateVerifiedForSunshineRestart = true;
                completed.Add(
                    "reloaded and verified the virtual display driver, then " +
                    $"reapplied the exact physical-only, PnP-disabled idle state for {string.Join(", ", physicalSnapshot.PhysicalDisplays)}");
            }
            catch (Exception error)
            {
                failures.Add($"virtual display reload: {error.Message}");
            }
        }

        if (restartSunshine &&
            sunshineStopped &&
            idleStateVerifiedForSunshineRestart)
        {
            try
            {
                WindowsServiceManager.Start(sunshineServiceName, "Sunshine");
                completed.Add("started Sunshine");
            }
            catch (Exception error)
            {
                failures.Add($"start Sunshine: {error.Message}");
            }
        }
        else if (restartSunshine && sunshineStopped)
        {
            failures.Add(
                "Sunshine was left stopped because the physical-only, PnP-disabled idle state could not be verified after recovery");
        }

        var message = string.Join("; ", completed.Concat(failures));
        return Record("recover-display-host", failures.Count == 0, message);
    }

    internal static HostRescueStatus RecordUnhandledFailure(
        string action,
        Exception error) =>
        Record(action, false, error.Message);

    internal static HostRescueStatus RecordResumeDecision(
        string trigger,
        bool success,
        string message) =>
        Record($"resume-display-check:{trigger}", success, message);

    internal static HostRescueStatus RecordSuspendDecision(
        bool success,
        string message) =>
        Record("power-suspend-display-prepare", success, message);

    internal static HostRescueStatus RecordSunshineExitDecision(
        bool success,
        string message) =>
        Record("sunshine-session-idle-recovery", success, message);

    internal static HostRescueStatus RecordPendingAudioRecoveryDecision(
        bool success,
        string message) =>
        Record("pending-audio-endpoint-recovery", success, message);

    private static HostRescueStatus Record(string action, bool success, string message)
    {
        var status = new HostRescueStatus(DateTimeOffset.UtcNow, action, success, message);
        try
        {
            MachineStateSecurity.SecureDiagnostics();
            TrustedFileSystem.WriteAllText(
                HostStatePaths.RescueStatusFile,
                JsonSerializer.Serialize(
                    status,
                    new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(HostStatePaths.RescueLogFile) &&
                new FileInfo(HostStatePaths.RescueLogFile).Length >=
                    MaximumRescueLogBytes)
            {
                TrustedFileSystem.WriteAllText(
                    HostStatePaths.RescueLogFile,
                    $"{DateTimeOffset.UtcNow:O}\tlog-rotation\tTrue\t" +
                    $"Previous sparse rescue log exceeded {MaximumRescueLogBytes} bytes.{Environment.NewLine}");
            }
            TrustedFileSystem.AppendAllText(
                HostStatePaths.RescueLogFile,
                $"{status.Timestamp:O}\t{status.Action}\t{status.Success}\t{status.Message}{Environment.NewLine}");
        }
        catch
        {
            // The recovery result is still returned even if diagnostic logging fails.
        }
        return status;
    }

}
