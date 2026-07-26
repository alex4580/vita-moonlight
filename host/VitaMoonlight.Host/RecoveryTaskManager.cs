using System.Diagnostics;

namespace VitaMoonlight.Host;

internal static class RecoveryTaskManager
{
    internal const string TaskName = "Vita Moonlight display recovery";

    internal static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            return Run("/Query", "/TN", TaskName) == 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void Install(string executablePath)
    {
        var taskCommand = $"\"{Path.GetFullPath(executablePath)}\" session recover";
        var exitCode = Run(
            "/Create", "/F",
            "/TN", TaskName,
            "/TR", taskCommand,
            "/SC", "ONLOGON",
            "/RL", "HIGHEST");
        if (exitCode != 0)
        {
            throw new InvalidOperationException("Windows could not create the display-recovery task.");
        }
        ConfigurePortableTaskSettings();
    }

    internal static void Uninstall()
    {
        if (!IsInstalled()) return;
        var exitCode = Run("/Delete", "/F", "/TN", TaskName);
        if (exitCode != 0)
        {
            throw new InvalidOperationException("Windows could not remove the display-recovery task.");
        }
    }

    private static int Run(params string[] arguments)
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

    private static void ConfigurePortableTaskSettings()
    {
        var powerShell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var script =
            $"$task = Get-ScheduledTask -TaskName '{TaskName}'; " +
            "$task.Settings.ExecutionTimeLimit = 'PT5M'; " +
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
            throw new TimeoutException("Windows did not finish configuring the display-recovery task.");
        }
        Task.WaitAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Windows could not configure display recovery for laptops and delayed logons.");
        }
    }
}
