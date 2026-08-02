using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitaMoonlight.Host;

internal enum ExactScheduledTaskState
{
    Missing,
    Present,
    Unknown,
}

internal sealed record ExactScheduledTaskProbe(
    ExactScheduledTaskState State,
    string? Error);

/// <summary>
/// Uses the Task Scheduler COM API so a missing exact task can be separated
/// from access denied, service failures, and other unknown query results.
/// `schtasks.exe` collapses those cases into the same nonzero exit code.
/// </summary>
internal static class ExactScheduledTaskManager
{
    private const int ErrorFileNotFound =
        unchecked((int)0x80070002);
    private const int ErrorPathNotFound =
        unchecked((int)0x80070003);
    private const int TaskNotRunning =
        unchecked((int)0x8004130B);

    internal static ExactScheduledTaskProbe Probe(string taskName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ExactScheduledTaskProbe(
                ExactScheduledTaskState.Unknown,
                "Task Scheduler is available only on Windows.");
        }

        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            (service, folder) = Connect();
            try
            {
                task = ((dynamic)folder).GetTask(taskName);
            }
            catch (Exception error) when (IsNotFound(error))
            {
                return new ExactScheduledTaskProbe(
                    ExactScheduledTaskState.Missing,
                    null);
            }
            return new ExactScheduledTaskProbe(
                ExactScheduledTaskState.Present,
                null);
        }
        catch (Exception error)
        {
            return new ExactScheduledTaskProbe(
                ExactScheduledTaskState.Unknown,
                error.Message);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    internal static bool DeleteExact(string taskName)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            (service, folder) = Connect();
            try
            {
                task = ((dynamic)folder).GetTask(taskName);
            }
            catch (Exception error) when (IsNotFound(error))
            {
                return false;
            }

            try
            {
                ((dynamic)task).Stop(0);
            }
            catch (Exception error) when (
                HasHResult(error, TaskNotRunning))
            {
                // An installed but idle task is already stopped.
            }
            catch (Exception error) when (IsNotFound(error))
            {
                // The exact task disappeared after GetTask. Continue to the
                // independent absence verification below.
            }

            try
            {
                ((dynamic)folder).DeleteTask(taskName, 0);
            }
            catch (Exception error) when (IsNotFound(error))
            {
                // A concurrent exact deletion is an idempotent success, but
                // still verify it rather than trusting the race outcome.
            }
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Windows could not remove scheduled task '{taskName}'. " +
                "Its state is unknown, so the host was retained.",
                error);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }

        var verification = Probe(taskName);
        return verification.State switch
        {
            ExactScheduledTaskState.Missing => true,
            ExactScheduledTaskState.Present =>
                throw new InvalidOperationException(
                    $"Windows retained scheduled task '{taskName}' after " +
                    "its exact deletion was accepted."),
            _ => throw new InvalidOperationException(
                $"Windows could not verify removal of scheduled task " +
                $"'{taskName}': {verification.Error ?? "unknown Task Scheduler error"}. " +
                "The host was retained."),
        };
    }

    internal static bool StopExact(string taskName)
    {
        object? service = null;
        object? folder = null;
        object? task = null;
        try
        {
            (service, folder) = Connect();
            try
            {
                task = ((dynamic)folder).GetTask(taskName);
            }
            catch (Exception error) when (IsNotFound(error))
            {
                return false;
            }

            try
            {
                ((dynamic)task).Stop(0);
            }
            catch (Exception error) when (
                HasHResult(error, TaskNotRunning))
            {
                // The exact task exists but has no running instance.
            }
            catch (Exception error) when (IsNotFound(error))
            {
                // The exact task was removed after GetTask.
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"Windows could not stop scheduled task '{taskName}'. " +
                "Its state is unknown, so no task was deleted.",
                error);
        }
        finally
        {
            ReleaseComObject(task);
            ReleaseComObject(folder);
            ReleaseComObject(service);
        }
    }

    internal static void RequireKnown(
        ExactScheduledTaskProbe probe,
        string taskName)
    {
        if (probe.State == ExactScheduledTaskState.Unknown)
        {
            throw new InvalidOperationException(
                $"Windows could not determine whether scheduled task " +
                $"'{taskName}' exists: {probe.Error ?? "unknown Task Scheduler error"}.");
        }
    }

    private static (object Service, object Folder) Connect()
    {
        var schedulerType = Type.GetTypeFromProgID(
            "Schedule.Service",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "Windows Task Scheduler COM service is unavailable.");
        var service = Activator.CreateInstance(schedulerType)
            ?? throw new InvalidOperationException(
                "Windows Task Scheduler COM service could not be created.");
        try
        {
            ((dynamic)service).Connect();
            var folder = ((dynamic)service).GetFolder("\\");
            return (service, folder);
        }
        catch
        {
            ReleaseComObject(service);
            throw;
        }
    }

    internal static bool IsNotFound(Exception error)
    {
        return HasHResult(
            error,
            ErrorFileNotFound,
            ErrorPathNotFound);
    }

    private static bool HasHResult(
        Exception error,
        params int[] expected)
    {
        for (Exception? current = error;
             current is not null;
             current = current.InnerException)
        {
            if (expected.Contains(current.HResult))
            {
                return true;
            }
        }
        return false;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}

internal static class RecoveryTaskManager
{
    internal const string TaskName = "Vita Moonlight display recovery";

    internal static bool IsInstalled()
    {
        return GetInstallationState().State ==
               ExactScheduledTaskState.Present;
    }

    internal static ExactScheduledTaskProbe GetInstallationState() =>
        ExactScheduledTaskManager.Probe(TaskName);

    internal static void Install(string executablePath)
    {
        var existing = GetInstallationState();
        ExactScheduledTaskManager.RequireKnown(existing, TaskName);
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
        ExactScheduledTaskManager.DeleteExact(TaskName);
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
            $"$task = Get-ScheduledTask -TaskName '{TaskName}' -TaskPath '\\'; " +
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
