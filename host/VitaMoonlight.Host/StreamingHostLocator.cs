using Microsoft.Win32;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace VitaMoonlight.Host;

internal static partial class StreamingHostLocator
{
    internal const string DefaultSunshineServiceName = "SunshineService";

    internal static string? FindSunshineExecutable() => FindExecutable(
        "sunshine.exe",
        Environment.GetEnvironmentVariable("SUNSHINE_PATH"),
        FindServiceExecutableDirectory(FindSunshineServiceName()),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Sunshine"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sunshine"));

    internal static string? FindApolloExecutable() => FindExecutable(
        "apollo.exe",
        Environment.GetEnvironmentVariable("APOLLO_PATH"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Apollo"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Apollo"))
        ?? FindExecutable(
            "sunshine.exe",
            Environment.GetEnvironmentVariable("APOLLO_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Apollo"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Apollo"));

    internal static bool IsApolloRunning()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (HasRunningProcessNamed("apollo") ||
            HasRunningProcessNamed("apollosvc"))
        {
            return true;
        }

        // Some Apollo packages retain Sunshine's executable name. In that
        // case compare the full image path instead of treating an unrelated
        // Sunshine process as Apollo.
        var apolloPath = FindApolloExecutable();
        if (apolloPath is null ||
            !Path.GetFileName(apolloPath).Equals(
                "sunshine.exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName("sunshine"))
        {
            using (process)
            {
                try
                {
                    var imagePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(imagePath))
                    {
                        // A matching candidate whose image cannot be
                        // inspected must be treated as running. Otherwise an
                        // elevated repair could race Apollo's configuration
                        // writer merely because Windows withheld its path.
                        return true;
                    }
                    if (string.Equals(
                            Path.GetFullPath(imagePath),
                            apolloPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception error) when (
                    error is InvalidOperationException or
                        System.ComponentModel.Win32Exception or
                        NotSupportedException)
                {
                    // An elevated cleanup must not race a process whose image
                    // identity Windows would not let it verify.
                    return true;
                }
            }
        }
        return false;
    }

    private static bool HasRunningProcessNamed(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Any(process => !process.HasExited);
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    internal static string FindSunshineServiceName()
    {
        if (ServiceExists(DefaultSunshineServiceName)) return DefaultSunshineServiceName;
        if (!OperatingSystem.IsWindows()) return DefaultSunshineServiceName;

        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return DefaultSunshineServiceName;
            foreach (var serviceName in services.GetSubKeyNames())
            {
                using var service = services.OpenSubKey(serviceName);
                var imagePath = service?.GetValue("ImagePath") as string;
                var executable = ExtractExecutablePath(imagePath);
                var fileName = executable is null ? null : Path.GetFileName(executable);
                if (fileName?.Equals("sunshinesvc.exe", StringComparison.OrdinalIgnoreCase) == true ||
                    fileName?.Equals("sunshine.exe", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return serviceName;
                }
            }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The default service name remains the safest actionable fallback.
        }
        return DefaultSunshineServiceName;
    }

    internal static string ResolveConfigurationDirectory(string? configuredDirectory, string hostMode)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return Path.GetFullPath(configuredDirectory);

        var environmentName = hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase)
            ? "APOLLO_CONFIG_DIR"
            : "SUNSHINE_CONFIG_DIR";
        var environmentDirectory = Environment.GetEnvironmentVariable(environmentName);
        if (!string.IsNullOrWhiteSpace(environmentDirectory)) return Path.GetFullPath(environmentDirectory);

        var executable = hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase)
            ? FindApolloExecutable()
            : FindSunshineExecutable();
        if (executable is not null)
        {
            return Path.Combine(Path.GetDirectoryName(executable)!, "config");
        }

        var folder = hostMode.Equals("apollo", StringComparison.OrdinalIgnoreCase) ? "Apollo" : "Sunshine";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder, "config");
    }

    internal static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            return closingQuote > 1 ? expanded[1..closingQuote] : null;
        }
        var match = ExecutablePathRegex().Match(expanded);
        return match.Success ? match.Groups["path"].Value.Trim() : null;
    }

    private static string? FindServiceExecutableDirectory(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var executable = ExtractExecutablePath(service?.GetValue("ImagePath") as string);
            return executable is null ? null : Path.GetDirectoryName(executable);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool ServiceExists(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return service is not null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static string? FindExecutable(string executableName, params string?[] candidates)
    {
        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            var expanded = Environment.ExpandEnvironmentVariables(candidate!);
            var path = Directory.Exists(expanded) ? Path.Combine(expanded, executableName) : expanded;
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        return null;
    }

    [GeneratedRegex(@"^(?<path>.*?\.exe)(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutablePathRegex();
}
