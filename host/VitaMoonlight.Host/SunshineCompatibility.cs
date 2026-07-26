using System.Diagnostics;

namespace VitaMoonlight.Host;

internal sealed record SunshineCompatibilityStatus(
    string? ExecutablePath,
    string? DetectedVersion,
    bool IsInstalled,
    bool IsSupported);

internal sealed record SunshineInstallResult(
    bool Changed,
    bool RestartRequired,
    string? DetectedVersion);

internal static class SunshineCompatibility
{
    internal const string MinimumVersionText = "2026.516.143833";
    private static readonly Version MinimumVersion = Version.Parse(MinimumVersionText);

    internal static SunshineCompatibilityStatus Inspect(string? executablePath = null)
    {
        var path = executablePath ?? StreamingHostLocator.FindSunshineExecutable();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return new SunshineCompatibilityStatus(null, null, false, false);
        }

        string? detectedVersion = null;
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(path);
            detectedVersion = versionInfo.ProductVersion ?? versionInfo.FileVersion;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The status below deliberately treats an unreadable version as
            // unsupported so native display keys are never written blindly.
        }

        return new SunshineCompatibilityStatus(
            Path.GetFullPath(path),
            detectedVersion,
            true,
            IsVersionSupported(detectedVersion));
    }

    internal static bool IsVersionSupported(string? versionText)
    {
        if (string.IsNullOrWhiteSpace(versionText)) return false;
        var normalized = versionText.Trim();
        var suffix = normalized.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0) normalized = normalized[..suffix];
        return Version.TryParse(normalized, out var version) &&
               version >= MinimumVersion;
    }

    internal static SunshineInstallResult EnsureCompatible(string installerPath)
    {
        var before = Inspect();
        if (before.IsSupported)
        {
            Console.WriteLine(
                $"Sunshine {before.DetectedVersion} already supports the required native display lifecycle.");
            return new SunshineInstallResult(false, false, before.DetectedVersion);
        }

        var fullInstallerPath = InstallationTrust.RequireBundledFile(
            installerPath,
            Path.Combine(
                "tools",
                "Sunshine",
                "Sunshine-Windows-AMD64-installer.msi"),
            "Sunshine installation");
        if (!File.Exists(fullInstallerPath))
        {
            throw new FileNotFoundException(
                "The pinned Sunshine installer is missing. Reinstall the Vita Moonlight host package.",
                fullInstallerPath);
        }

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(systemDirectory, "msiexec.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/i");
        process.StartInfo.ArgumentList.Add(fullInstallerPath);
        process.StartInfo.ArgumentList.Add("/qn");
        process.StartInfo.ArgumentList.Add("/norestart");
        process.Start();
        if (!process.WaitForExit(checked((int)TimeSpan.FromMinutes(5).TotalMilliseconds)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Sunshine installer did not finish within five minutes.");
        }
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException(
                $"The pinned Sunshine installation failed with Windows Installer code {process.ExitCode}.");
        }

        var after = Inspect();
        if (process.ExitCode == 3010)
        {
            Console.WriteLine(
                "The Sunshine installer completed and Windows requires a restart before its final version and service state can be verified.");
            return new SunshineInstallResult(
                true,
                true,
                after.DetectedVersion);
        }
        if (!after.IsSupported)
        {
            throw new InvalidOperationException(
                $"Sunshine {MinimumVersionText} or newer is required for safe virtual-display switching. " +
                $"Detected: {after.DetectedVersion ?? "unknown"}. Remove a stale SUNSHINE_PATH override or repair Sunshine.");
        }

        Console.WriteLine(
            $"Installed compatible Sunshine {after.DetectedVersion}. No restart was requested.");
        return new SunshineInstallResult(
            true,
            false,
            after.DetectedVersion);
    }
}
