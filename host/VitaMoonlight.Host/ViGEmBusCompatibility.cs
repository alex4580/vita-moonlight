using System.Diagnostics;

namespace VitaMoonlight.Host;

internal sealed record ViGEmBusInstallResult(
    bool Changed,
    bool RestartRequired,
    WindowsServiceState ServiceState);

internal static class ViGEmBusCompatibility
{
    internal static ViGEmBusInstallResult EnsureCompatible(string installerPath)
    {
        var before = WindowsServiceManager.GetState("ViGEmBus");
        if (before == WindowsServiceState.Running)
        {
            Console.WriteLine("ViGEmBus is installed and running.");
            return new ViGEmBusInstallResult(false, false, before);
        }

        var fullInstallerPath = InstallationTrust.RequireBundledFile(
            installerPath,
            Path.Combine(
                "tools",
                "ViGEmBus",
                "ViGEmBus_1.22.0_x64_x86_arm64.exe"),
            "ViGEmBus installation");
        if (!File.Exists(fullInstallerPath))
        {
            throw new FileNotFoundException(
                "The pinned ViGEmBus installer is missing. Reinstall the Vita Moonlight host package.",
                fullInstallerPath);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fullInstallerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/qn");
        process.StartInfo.ArgumentList.Add("/norestart");
        if (!process.Start())
        {
            throw new InvalidOperationException("The ViGEmBus installer could not be started.");
        }
        if (!process.WaitForExit(checked((int)TimeSpan.FromMinutes(5).TotalMilliseconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The ViGEmBus installer did not finish within five minutes.");
        }
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException(
                $"The pinned ViGEmBus installation failed with exit code {process.ExitCode}.");
        }

        var after = WindowsServiceManager.GetState("ViGEmBus");
        if (process.ExitCode == 3010)
        {
            Console.WriteLine(
                "ViGEmBus was installed or repaired, and Windows requires a restart before its service can be verified.");
            return new ViGEmBusInstallResult(true, true, after);
        }
        if (after == WindowsServiceState.NotInstalled)
        {
            throw new InvalidOperationException(
                "ViGEmBus is still missing after its installer completed.");
        }

        if (after != WindowsServiceState.Running)
        {
            WindowsServiceManager.Start("ViGEmBus", "ViGEmBus");
            after = WindowsServiceManager.GetState("ViGEmBus");
        }
        if (after != WindowsServiceState.Running)
        {
            throw new InvalidOperationException(
                $"ViGEmBus repair completed, but its service is {after} instead of Running.");
        }

        Console.WriteLine("ViGEmBus was installed or repaired and is running.");
        return new ViGEmBusInstallResult(true, false, after);
    }
}
