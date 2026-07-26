namespace VitaMoonlight.Host;

internal static class InstallationTrust
{
    private static bool allowSelfTestPaths;

    internal static string ExpectedInstallationDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        "Vita Moonlight Host");

    internal static string ExpectedExecutablePath => Path.Combine(
        ExpectedInstallationDirectory,
        "VitaMoonlight.Host.exe");

    internal static bool IsInstalledPayload(out string reason)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            reason = "The running executable path is unavailable.";
            return false;
        }

        var actual = Path.GetFullPath(executable);
        var expected = Path.GetFullPath(ExpectedExecutablePath);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            reason =
                "This is the diagnostics-only portable companion. Install " +
                "Vita Moonlight Host before running administrator setup, " +
                "driver, recovery-task, or uninstall actions.";
            return false;
        }

        try
        {
            var installDirectory = Path.GetDirectoryName(actual)
                ?? throw new InvalidDataException(
                    "The installed executable has no parent directory.");
            if ((File.GetAttributes(installDirectory) &
                 FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(actual) &
                 FileAttributes.ReparsePoint) != 0)
            {
                reason =
                    "The installed host path is a reparse point. Repair the " +
                    "host with the signed installer before continuing.";
                return false;
            }
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException)
        {
            reason =
                $"Windows could not validate the installed host path: " +
                $"{error.Message}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static void RequireInstalledPayload(string operation)
    {
        if (!IsInstalledPayload(out var reason))
        {
            throw new InvalidOperationException(
                $"{operation} is available only from the protected Program " +
                $"Files installation. {reason}");
        }
    }

    internal static string RequireBundledFile(
        string suppliedPath,
        string relativePath,
        string operation)
    {
        RequireInstalledPayload(operation);
        return RequireBundledReadOnlyFile(
            suppliedPath,
            relativePath,
            operation);
    }

    internal static string RequireBundledReadOnlyFile(
        string suppliedPath,
        string relativePath,
        string operation)
    {
        var actual = Path.GetFullPath(suppliedPath);
        var expected = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, relativePath));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{operation} refused an external executable or installer. " +
                "Repair or re-extract the host package so its bundled " +
                $"copy is available at {expected}.");
        }
        if (!File.Exists(expected))
        {
            throw new InvalidDataException(
                $"The bundled file is missing: {expected}");
        }

        var packageRoot = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var candidate = expected;
        while (true)
        {
            if ((File.GetAttributes(candidate) &
                 FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"The bundled file path is redirected: {candidate}");
            }
            if (candidate.Equals(
                    packageRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            candidate = Path.GetDirectoryName(candidate)
                ?? throw new InvalidDataException(
                    $"The bundled file is outside its package root: {expected}");
            if (!IsPathWithin(candidate, packageRoot) &&
                !candidate.Equals(
                    packageRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"The bundled file is outside its package root: {expected}");
            }
        }
        return expected;
    }

    internal static string RequireTrustedConfigurationDirectory(
        string suppliedPath,
        string operation)
    {
        var fullPath = Path.GetFullPath(suppliedPath)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        if (allowSelfTestPaths ||
            !IsInstalledPayload(out _))
        {
            // Self-test uses an isolated temporary tree and never enters
            // through an operational command.
            return fullPath;
        }

        var allowedRoots = new[]
        {
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86),
        };
        if (!allowedRoots.Any(root =>
                IsPathWithin(fullPath, root)))
        {
            throw new InvalidDataException(
                $"{operation} requires a system-wide configuration " +
                "directory under Program Files. Per-user and " +
                "redirected configuration paths are not used by elevated " +
                "host integration.");
        }
        return fullPath;
    }

    internal static IDisposable AllowSelfTestPaths()
    {
        if (allowSelfTestPaths)
        {
            throw new InvalidOperationException(
                "The installation-trust self-test scope is already active.");
        }
        allowSelfTestPaths = true;
        return new SelfTestPathScope();
    }

    private static bool IsPathWithin(
        string path,
        string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var relative = Path.GetRelativePath(fullRoot, path);
        return relative != ".." &&
               !relative.StartsWith(
                   $"..{Path.DirectorySeparatorChar}",
                   StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }

    private sealed class SelfTestPathScope : IDisposable
    {
        public void Dispose()
        {
            allowSelfTestPaths = false;
        }
    }
}
