using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal sealed record WindowsPlatformStatus(
    bool IsWindows,
    bool MeetsMinimumVersion,
    Architecture OperatingSystemArchitecture,
    Architecture ProcessArchitecture,
    string? InstallationType)
{
    internal bool IsSupported => WindowsPlatformCompatibility.IsSupported(
        IsWindows,
        MeetsMinimumVersion,
        OperatingSystemArchitecture,
        ProcessArchitecture,
        InstallationType);

    internal string ArchitectureDescription =>
        $"{OperatingSystemArchitecture} OS / {ProcessArchitecture} process";

    internal string RequirementMessage =>
        !IsWindows
            ? "Vita Moonlight Host is supported only on Windows."
            : !MeetsMinimumVersion
                ? "Vita Moonlight Host requires Windows 10 version 2004 (build 19041) or newer, or Windows 11."
                : OperatingSystemArchitecture != Architecture.X64 ||
                  ProcessArchitecture != Architecture.X64
                    ? "Vita Moonlight Host requires x64 Windows on an Intel or AMD CPU. ARM64 and x86 packages are not bundled."
                    : !string.Equals(InstallationType, "Client", StringComparison.OrdinalIgnoreCase)
                        ? string.IsNullOrWhiteSpace(InstallationType)
                            ? "Vita Moonlight Host could not verify a supported client edition of Windows. Windows Server is not supported."
                            : $"Vita Moonlight Host requires client Windows 10 or 11. Windows installation type '{InstallationType}' is not supported."
                        : string.Empty;
}

internal static class WindowsPlatformCompatibility
{
    private const string CurrentVersionRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    internal static WindowsPlatformStatus Inspect()
    {
        var isWindows = OperatingSystem.IsWindows();
        return new WindowsPlatformStatus(
            isWindows,
            isWindows && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
            RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture,
            isWindows ? ReadInstallationType() : null);
    }

    internal static bool IsSupported(
        bool isWindows,
        bool meetsMinimumVersion,
        Architecture operatingSystemArchitecture,
        Architecture processArchitecture,
        string? installationType) =>
        isWindows &&
        meetsMinimumVersion &&
        operatingSystemArchitecture == Architecture.X64 &&
        processArchitecture == Architecture.X64 &&
        string.Equals(installationType, "Client", StringComparison.OrdinalIgnoreCase);

    private static string? ReadInstallationType()
    {
        try
        {
            using var localMachine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var currentVersion = localMachine.OpenSubKey(CurrentVersionRegistryPath);
            return currentVersion?.GetValue("InstallationType")?.ToString()?.Trim();
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return null;
        }
    }
}
