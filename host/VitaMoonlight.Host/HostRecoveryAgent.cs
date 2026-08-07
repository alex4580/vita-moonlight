using System.Diagnostics;
using System.Runtime.InteropServices;
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
            return 0;
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

    internal static bool IsRunning() => IsProcessPresent() && IsEventSignaled(ReadyEventName);

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
        if (RunTask("/Run", "/TN", TaskName) != 0)
        {
            throw new InvalidOperationException("Windows created the stream rescue agent but could not start it.");
        }
        for (var attempt = 0; attempt < 100 && !IsRunning(); attempt++) Thread.Sleep(100);
        if (!IsRunning())
        {
            throw new InvalidOperationException(
                "Windows started the stream rescue task, but its mandatory display-recovery hotkey did not become ready.");
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

    internal HostRecoveryAgentContext()
    {
        window = new HostRecoveryHotkeyWindow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) window.Dispose();
        base.Dispose(disposing);
    }
}

internal enum ResumeTopologyDecision
{
    Wait,
    Healthy,
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
        bool minimumRecoveryAgeReached)
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
            return stableSamples >= HealthySamplesRequired
                ? ResumeTopologyDecision.Healthy
                : ResumeTopologyDecision.Wait;
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
    private static readonly TimeSpan ResumeInitialDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResumeFollowupDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ResumeSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumRecoveryAge = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SuspendDisplayLeaseBudget =
        TimeSpan.FromMilliseconds(1500);
    private const int ResumeSampleAttempts = 12;
    private const int SuspendActionWaitMilliseconds = 500;
    private const int DisplayActionRunning = 1;
    private readonly HashSet<int> registeredHotkeys = new();
    private readonly List<EventWaitHandle> modeReadinessEvents = new();
    private readonly object resumeInspectionSync = new();
    private CancellationTokenSource? resumeInspectionCancellation;
    private DateTimeOffset resumeObservationStartedAt;
    private DateTimeOffset resumeObservationEndsAt;
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
            RegisterRequiredHotkey(RecoverDisplayHotkeyId, modifiers, VkF11, "display-recovery");
            if (HostRecoveryAgentManager.LegacyModeHotkeysRequired(HostSettings.Load()))
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
                currentSuspendIntent = null;
                currentSuspendIntent =
                    DisplaySuspendIntentStore.BeginForCurrentAgent();
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
        else if (message.Msg == WmDisplayChange && IsResumeObservationOpen())
        {
            ScheduleResumeInspection("resume-display-change", beginObservation: false);
        }
        else if (message.Msg == WmDeviceChange && IsResumeObservationOpen())
        {
            ScheduleResumeInspection("resume-device-change", beginObservation: false);
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
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
    }

    private bool IsResumeObservationOpen()
    {
        lock (resumeInspectionSync)
        {
            return !disposed && DateTimeOffset.UtcNow < resumeObservationEndsAt;
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
            ? CapturePendingTransactionMarker()
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
                resumePendingTransactionAtWake = currentPendingTransaction;
            }
            else if (now >= resumeObservationEndsAt)
            {
                if (Volatile.Read(ref currentSuspendIntent) is null)
                {
                    return false;
                }
                // A live durable suspend token must not become a permanent
                // block merely because another process held session.lock past
                // the ordinary topology-observation window. Keep bounded
                // individual attempts, but continue them until physical-only
                // verification commits the token or the agent exits.
                resumeObservationEndsAt = now + ResumeObservationWindow;
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
                            "suspend-fence-pending"),
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
                    observationAge >= MinimumRecoveryAge);
                if (decision == ResumeTopologyDecision.Healthy)
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
                minimumRecoveryAgeReached: true);
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
        var pendingTransactionMarker = CapturePendingTransactionMarker();
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
            $"{pendingTransactionMarker ?? "<none>"}:{pathSignature}");
    }

    private static string? CapturePendingTransactionMarker()
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
        string Signature)
    {
        internal string Summary =>
            $"active paths={ActivePaths}, physical={ActivePhysicalPaths}, " +
            $"managed Vita VDD={ActiveManagedVirtualPaths}, other virtual={ActiveOtherVirtualPaths}, " +
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
    private static int unfencedSuspendFallbackRunning;
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
            DateTimeOffset? resumeObservedAt = null;
            try
            {
                while (true)
                {
                    var stillSuspending = suspendIsPending();
                    if (!stillSuspending)
                    {
                        resumeObservedAt ??= DateTimeOffset.UtcNow;
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

                    if (resumeObservedAt is { } resumed &&
                        DateTimeOffset.UtcNow - resumed >
                            TimeSpan.FromSeconds(30))
                    {
                        Record(
                            "unfenced-suspend-recovery",
                            false,
                            "Durable suspend-fence publication failed and the fallback could not verify physical-only within 30 seconds after resume: " +
                            (lastError?.Message ?? "unknown display recovery error"));
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
                .RestoreIfPendingLocked(transaction);
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
            var displays = topology.ListDisplays();
            var hasActivePhysical = displays.Any(display =>
                display.IsActive &&
                display.IsAvailable &&
                !DisplayTopologyService.IsLikelyVirtualDisplay(display));
            var hasActiveManagedVirtual = displays.Any(display =>
                display.IsActive &&
                DisplayTopologyService.IsManagedVirtualDisplay(display));
            IReadOnlyList<string> physicalDisplays;
            PhysicalDisplayModeRepairResult modeRepair;
            var changedTopology = false;

            if (!hasActivePhysical || hasActiveManagedVirtual)
            {
                physicalDisplays = topology.RecoverPhysicalDisplays(
                    out modeRepair);
                topology.DisableManagedVirtualDisplays();
                changedTopology = true;
            }
            else
            {
                physicalDisplays = displays
                    .Where(display =>
                        display.IsActive &&
                        display.IsAvailable &&
                        !DisplayTopologyService.IsLikelyVirtualDisplay(display))
                    .Select(display => string.IsNullOrWhiteSpace(display.FriendlyName)
                        ? display.DevicePath
                        : display.FriendlyName)
                    .ToArray();
                modeRepair =
                    topology.RestorePersistedPhysicalDisplayModes();
            }

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
            work.Add(changedTopology
                ? "activated the physical display topology with the managed Vita VDD inactive"
                : "confirmed a physical display is active and the managed Vita VDD is inactive");
            if (modeRepair.RestoredModes.Count > 0)
            {
                work.Add(
                    "restored persisted physical display mode(s): " +
                    string.Join(", ", modeRepair.RestoredModes));
            }
            if (modeRepair.Warnings.Count > 0)
            {
                work.Add(
                    "physical mode repair warnings: " +
                    string.Join(", ", modeRepair.Warnings));
            }
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

        if (restartSunshine)
        {
            try
            {
                WindowsServiceManager.Stop(sunshineServiceName, "Sunshine");
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
            sunshine &&
            DisplayWizardAdapter.IsDriverInstalled())
        {
            try
            {
                DisplayWizardAdapter
                    .LocateBundledForUninstall()
                    .ReloadDriver(transaction);
                completed.Add("reloaded the virtual display driver");
                IReadOnlyList<string>? physical = null;
                for (var attempt = 0; attempt < 10 && physical is null; attempt++)
                {
                    try
                    {
                        physical = new DisplayTopologyService().RecoverPhysicalDisplays();
                    }
                    catch when (attempt < 9)
                    {
                        Thread.Sleep(250);
                    }
                }
                completed.Add($"reapplied physical-only topology for {string.Join(", ", physical!)}");
            }
            catch (Exception error)
            {
                failures.Add($"virtual display reload: {error.Message}");
            }
        }

        if (restartSunshine)
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
