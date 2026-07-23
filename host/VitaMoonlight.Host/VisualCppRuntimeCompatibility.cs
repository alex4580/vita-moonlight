using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal sealed record VisualCppRuntimeStatus(
    bool IsInstalled,
    string? DetectedVersion,
    bool IsSupported);

internal sealed record VisualCppRuntimeInstallResult(
    bool Changed,
    bool RestartRequired,
    string? DetectedVersion);

internal static class VisualCppRuntimeCompatibility
{
    internal const string MinimumVersionText = "14.44.35211.0";
    private const string RuntimeRegistryPath =
        @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
    private static readonly Version MinimumVersion = Version.Parse(MinimumVersionText);

    internal static VisualCppRuntimeStatus Inspect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new VisualCppRuntimeStatus(false, null, false);
        }

        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var runtime = localMachine.OpenSubKey(RuntimeRegistryPath);
            var installed = runtime?.GetValue("Installed") is int installedValue &&
                            installedValue == 1;
            var detectedVersion = runtime?.GetValue("Version")?.ToString();
            return new VisualCppRuntimeStatus(
                installed,
                detectedVersion,
                installed && IsVersionSupported(detectedVersion));
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new VisualCppRuntimeStatus(false, null, false);
        }
    }

    internal static bool IsVersionSupported(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return false;
        var normalized = versionText.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out var version) &&
               version >= MinimumVersion;
    }

    internal static VisualCppRuntimeInstallResult EnsureCompatible(string installerPath)
    {
        var before = Inspect();
        if (before.IsSupported)
        {
            Console.WriteLine(
                $"Microsoft Visual C++ runtime {before.DetectedVersion} is already compatible.");
            return new VisualCppRuntimeInstallResult(
                false,
                false,
                before.DetectedVersion);
        }

        var fullInstallerPath = Path.GetFullPath(installerPath);
        if (!File.Exists(fullInstallerPath))
        {
            throw new FileNotFoundException(
                "The packaged Microsoft Visual C++ runtime installer is missing. " +
                "Extract the complete portable ZIP or reinstall Vita Moonlight Host.",
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
        process.StartInfo.ArgumentList.Add("/install");
        process.StartInfo.ArgumentList.Add("/quiet");
        process.StartInfo.ArgumentList.Add("/norestart");
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "The Microsoft Visual C++ runtime installer could not be started.");
        }
        if (!process.WaitForExit(checked((int)TimeSpan.FromMinutes(5).TotalMilliseconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                "The Microsoft Visual C++ runtime installer did not finish within five minutes.");
        }
        if (process.ExitCode is not 0 and not 1641 and not 3010)
        {
            throw new InvalidOperationException(
                $"The Microsoft Visual C++ runtime installation failed with exit code {process.ExitCode}.");
        }

        var after = Inspect();
        if (process.ExitCode is 1641 or 3010)
        {
            Console.WriteLine(
                "The Microsoft Visual C++ runtime was installed or repaired, and Windows requires a restart.");
            return new VisualCppRuntimeInstallResult(
                true,
                true,
                after.DetectedVersion);
        }
        if (!after.IsSupported)
        {
            throw new InvalidOperationException(
                $"Microsoft Visual C++ runtime {MinimumVersionText} or newer is required by the virtual display driver. " +
                $"Detected: {after.DetectedVersion ?? "not installed"}.");
        }

        Console.WriteLine(
            $"Installed compatible Microsoft Visual C++ runtime {after.DetectedVersion}.");
        return new VisualCppRuntimeInstallResult(
            true,
            false,
            after.DetectedVersion);
    }
}
