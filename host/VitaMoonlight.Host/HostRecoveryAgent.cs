using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record HostRescueStatus(
    DateTimeOffset Timestamp,
    string Action,
    bool Success,
    string Message);

internal static class HostRecoveryAgentManager
{
    internal const string TaskName = "Vita Moonlight stream rescue agent";
    internal const string MutexName = @"Local\VitaMoonlight.StreamRescueAgent";

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

        try
        {
            Application.Run(new HostRecoveryAgentContext());
            return 0;
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    internal static bool IsInstalled() => RunTask("/Query", "/TN", TaskName) == 0;

    internal static bool IsRunning()
    {
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

    internal static void Install(string executablePath)
    {
        if (IsInstalled())
        {
            RunTask("/End", "/TN", TaskName);
            for (var attempt = 0; attempt < 20 && IsRunning(); attempt++) Thread.Sleep(100);
        }
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
        for (var attempt = 0; attempt < 30 && !IsRunning(); attempt++) Thread.Sleep(100);
        if (!IsRunning())
        {
            throw new InvalidOperationException("Windows started the stream rescue task, but its hotkey agent did not remain running.");
        }
    }

    internal static void Uninstall()
    {
        if (!IsInstalled()) return;
        RunTask("/End", "/TN", TaskName);
        for (var attempt = 0; attempt < 20 && IsRunning(); attempt++) Thread.Sleep(100);
        if (RunTask("/Delete", "/F", "/TN", TaskName) != 0)
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

    private static void ConfigurePersistentTask()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShell = Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script =
            $"$task = Get-ScheduledTask -TaskName '{TaskName}'; " +
            "$task.Settings.ExecutionTimeLimit = 'PT0S'; " +
            "$task.Settings.DisallowStartIfOnBatteries = $false; " +
            "$task.Settings.StopIfGoingOnBatteries = $false; " +
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
    private const int WmHotkey = 0x0312;
    private const int CloseForegroundHotkeyId = 1;
    private const int RecoverDisplayHotkeyId = 2;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private int actionRunning;

    internal HostRecoveryHotkeyWindow()
    {
        CreateHandle(new CreateParams { Caption = "Vita Moonlight stream rescue agent" });
        var modifiers = ModAlt | ModControl | ModShift | ModNoRepeat;
        if (!RegisterHotKey(Handle, CloseForegroundHotkeyId, modifiers, VkF12) ||
            !RegisterHotKey(Handle, RecoverDisplayHotkeyId, modifiers, VkF11))
        {
            Dispose();
            throw new InvalidOperationException("The Vita Moonlight rescue hotkeys could not be registered.");
        }
    }

    protected override void WndProc(ref Message message)
    {
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
        if (Handle == IntPtr.Zero) return;
        UnregisterHotKey(Handle, CloseForegroundHotkeyId);
        UnregisterHotKey(Handle, RecoverDisplayHotkeyId);
        DestroyHandle();
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
        "csrss", "dwm", "explorer", "fontdrvhost", "lsass", "services", "sihost",
        "smss", "steam", "sunshine", "sunshinesvc", "svchost", "system",
        "taskhostw", "VitaMoonlight.Host", "wininit", "winlogon",
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

    internal static HostRescueStatus RecoverDisplayAndStreamingHost()
    {
        var failures = new List<string>();
        var completed = new List<string>();
        var settings = HostSettings.Load();
        var sunshine = settings.HostMode.Equals("sunshine", StringComparison.OrdinalIgnoreCase);

        if (sunshine)
        {
            try
            {
                WindowsServiceManager.Stop("SunshineService", "Sunshine");
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
                WindowsServiceManager.Start("SunshineService", "Sunshine");
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
