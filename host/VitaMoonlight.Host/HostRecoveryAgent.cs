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

    internal static bool IsInstalled() => RunTask("/Query", "/TN", TaskName) == 0;

    internal static bool IsRunning() => IsProcessPresent() && IsEventSignaled(ReadyEventName);

    internal static IReadOnlyList<HostModeHotkeyStatus> GetModeHotkeyReadiness()
    {
        var agentReady = IsRunning();
        return VitaDisplayModes.Supported
            .Select(mode => new HostModeHotkeyStatus(
                mode,
                agentReady && IsEventSignaled(ModeHotkeyReadyEventName(mode))))
            .ToArray();
    }

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
        if (IsInstalled())
        {
            RunTask("/End", "/TN", TaskName);
        }
        StopCurrentSessionAgent();
        var taskCommand = $"\"{Path.GetFullPath(executablePath)}\" agent run --background";
        var createExitCode = RunTask(
            "/Create", "/F",
            "/TN", TaskName,
            "/TR", taskCommand,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST");
        if (createExitCode != 0)
        {
            throw new InvalidOperationException("Windows could not create the stream rescue agent task.");
        }
        ConfigurePersistentTask();
        if (RunTask("/Run", "/TN", TaskName) != 0)
        {
            throw new InvalidOperationException("Windows created the stream rescue agent but could not start it.");
        }
        for (var attempt = 0; attempt < 100 && !IsRunning(); attempt++) Thread.Sleep(100);
        if (!IsRunning())
        {
            throw new InvalidOperationException(
                "Windows started the stream rescue task, but its mandatory close-game and display-recovery hotkeys did not become ready.");
        }
    }

    internal static void Uninstall()
    {
        var installed = IsInstalled();
        if (installed) RunTask("/End", "/TN", TaskName);
        StopCurrentSessionAgent();
        if (installed && RunTask("/Delete", "/F", "/TN", TaskName) != 0)
        {
            throw new InvalidOperationException("Windows could not remove the stream rescue agent task.");
        }
    }

    internal static HostRescueStatus? ReadLastStatus()
    {
        try
        {
            return File.Exists(HostStatePaths.RescueStatusFile)
                ? JsonSerializer.Deserialize<HostRescueStatus>(File.ReadAllText(HostStatePaths.RescueStatusFile))
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
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
            $"$task = Get-ScheduledTask -TaskName '{TaskName}'; " +
            "$task.Settings.ExecutionTimeLimit = 'PT0S'; " +
            "$task.Settings.DisallowStartIfOnBatteries = $false; " +
            "$task.Settings.StopIfGoingOnBatteries = $false; " +
            "$task.Settings.StartWhenAvailable = $true; " +
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

internal sealed class HostRecoveryHotkeyWindow : NativeWindow, IDisposable
{
    private const int WmClose = 0x0010;
    private const int WmHotkey = 0x0312;
    private const int CloseForegroundHotkeyId = 1;
    private const int RecoverDisplayHotkeyId = 2;
    private const int Mode960x540HotkeyId = 3;
    private const int Mode960x544HotkeyId = 4;
    private const int Mode1280x720HotkeyId = 5;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private const uint VkF8 = 0x77;
    private const uint VkF9 = 0x78;
    private const uint VkF10 = 0x79;
    private readonly HashSet<int> registeredHotkeys = new();
    private readonly List<EventWaitHandle> modeReadinessEvents = new();
    private int actionRunning;
    private bool disposed;

    internal HostRecoveryHotkeyWindow()
    {
        CreateHandle(new CreateParams { Caption = HostRecoveryAgentManager.WindowCaption });
        var modifiers = ModAlt | ModControl | ModShift | ModNoRepeat;
        try
        {
            RegisterRequiredHotkey(CloseForegroundHotkeyId, modifiers, VkF12, "close-game");
            RegisterRequiredHotkey(RecoverDisplayHotkeyId, modifiers, VkF11, "display-recovery");
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
        if (message.Msg == WmHotkey && Interlocked.CompareExchange(ref actionRunning, 1, 0) == 0)
        {
            var hotkeyId = message.WParam.ToInt32();
            _ = Task.Run(() =>
            {
                try
                {
                    if (hotkeyId == CloseForegroundHotkeyId)
                    {
                        HostRecoveryActions.CloseForegroundApplication();
                    }
                    else if (hotkeyId == RecoverDisplayHotkeyId)
                    {
                        HostRecoveryActions.RecoverDisplayAndStreamingHost();
                    }
                    else if (ModeForHotkeyId(hotkeyId) is { } mode)
                    {
                        HostRecoveryActions.ChangeVirtualDisplayMode(mode);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref actionRunning, 0);
                }
            });
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
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
    private static readonly HashSet<string> ProtectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "apollo", "apollosvc", "audiodg", "csrss", "ctfmon",
        "dwm", "explorer", "fontdrvhost", "lockapp", "logonui", "lsass", "runtimebroker",
        "searchhost", "searchindexer", "securityhealthservice", "securityhealthsystray",
        "services", "shellexperiencehost", "sihost", "smss", "startmenuexperiencehost",
        "steam", "sunshine", "sunshinesvc", "svchost", "system", "taskhostw", "taskmgr",
        "textinputhost", "userinit", "VitaMoonlight.Host", "wininit", "winlogon",
    };

    internal static bool IsProtectedProcessName(string processName) => ProtectedProcessNames.Contains(processName);

    internal static HostRescueStatus CloseForegroundApplication()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero)
            {
                return Record("close-foreground", false, "Windows did not report a foreground application.");
            }
            GetWindowThreadProcessId(window, out var processId);
            if (processId <= 4 || processId == Environment.ProcessId)
            {
                return Record("close-foreground", false, "The foreground process is protected and was not closed.");
            }

            using var process = Process.GetProcessById(checked((int)processId));
            var processName = process.ProcessName;
            if (IsProtectedProcessName(processName))
            {
                return Record("close-foreground", false, $"Refused to close protected process {processName}.");
            }

            var closedGracefully = process.CloseMainWindow() && process.WaitForExit(1500);
            if (!closedGracefully && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000))
                {
                    return Record("close-foreground", false, $"Windows did not confirm that {processName} exited.");
                }
            }
            return Record(
                "close-foreground",
                true,
                closedGracefully
                    ? $"Closed {processName} normally."
                    : $"Force-closed {processName} and its child processes.");
        }
        catch (Exception error)
        {
            return Record("close-foreground", false, error.Message);
        }
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

    internal static HostRescueStatus RecoverDisplayAndStreamingHost()
    {
        var failures = new List<string>();
        var completed = new List<string>();
        var settings = HostSettings.Load();
        var sunshine = settings.HostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase);
        var sunshineServiceName = StreamingHostLocator.FindSunshineServiceName();

        if (sunshine)
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

        try
        {
            if (new SessionManager().RestoreIfPending()) completed.Add("restored the saved display transaction");
        }
        catch (Exception error)
        {
            failures.Add($"saved display recovery: {error.Message}");
        }

        try
        {
            var physical = new DisplayTopologyService().RecoverPhysicalDisplays();
            completed.Add($"activated physical display {string.Join(", ", physical)}");
        }
        catch (Exception error)
        {
            failures.Add($"physical display recovery: {error.Message}");
        }

        if (sunshine && DisplayWizardAdapter.IsDriverInstalled())
        {
            try
            {
                DisplayWizardAdapter.Locate(settings.DisplayWizardPath).ReloadDriver();
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

        if (sunshine)
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

    private static HostRescueStatus Record(string action, bool success, string message)
    {
        var status = new HostRescueStatus(DateTimeOffset.UtcNow, action, success, message);
        try
        {
            Directory.CreateDirectory(HostStatePaths.Root);
            DisplayTopologyService.AtomicWrite(
                HostStatePaths.RescueStatusFile,
                JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
            File.AppendAllText(
                HostStatePaths.RescueLogFile,
                $"{status.Timestamp:O}\t{status.Action}\t{status.Success}\t{status.Message}{Environment.NewLine}");
        }
        catch
        {
            // The recovery result is still returned even if diagnostic logging fails.
        }
        return status;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
