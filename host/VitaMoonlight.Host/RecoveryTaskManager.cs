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
}
