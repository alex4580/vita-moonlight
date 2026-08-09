using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal sealed record InstallResidueCleanupResult(
    int RemovedFiles,
    int RemovedDirectories,
    IReadOnlyList<string> RetainedOwnedEntries,
    IReadOnlyList<string> RetainedDirectories)
{
    internal bool Completed => RetainedOwnedEntries.Count == 0;
}

/// <summary>
/// Removes only payload and ProgramData names shipped by known older Vita
/// Moonlight releases. Each cleanup generation is immutable and exact; future
/// releases append a new generation instead of broadening an old one.
/// </summary>
internal static class InstallResidueCleanup
{
    private const string RegistryPath = @"SOFTWARE\VitaMoonlight\Host";
    private const string CleanupVersionValue = "InstallResidueCleanupVersion";
    internal const int CurrentCleanupVersion = 1;

    // These documentation files were installed at the application root by
    // releases through 0.14.6. They now live in host/ or docs/. README.md is
    // intentionally absent because the current package owns that root name.
    internal static readonly string[] ObsoleteRootPayloadFilesV1 =
    [
        "COMPATIBILITY.md",
        "END_TO_END_TEST.md",
        "FINAL_RELEASE_CHECKLIST.md",
        "THIRD_PARTY_NOTICES.md",
        "VITA_SETTINGS_GUIDE.md",
    ];

    // Version 0.14.6 and earlier wrote machine state directly beneath
    // C:\ProgramData\VitaMoonlight. The current product never reads these
    // entries. Do not add patterns, subpaths, or user-controlled names.
    internal static readonly string[] LegacyProgramDataStateFilesV1 =
    [
        "display-recovery.json",
        "host-settings.json",
        "session.lock",
        "last-command-error.txt",
        "display-driver-verification.json",
        "display-driver-directory-identity.json",
        "backend-lifecycle.json",
        "backend-lifecycle.backup.json",
        "backend-lifecycle.lock",
        "backend-disabled.intent",
        "deferred-host-setup.json",
        "display-suspend.intent",
        "display-suspend.lock",
        "installer-maintenance.json",
        "installer-maintenance.backup.json",
        "installer-maintenance.lock",
        "stream-rescue-status.json",
        "stream-rescue.log",
    ];

    internal static readonly string[] LegacyProgramDataDiagnosticsFilesV1 =
    [
        "stream-rescue-status.json",
        "stream-rescue.log",
    ];

    internal static void RunForInstalledPayloadIfNeeded()
    {
        if (!OperatingSystem.IsWindows() ||
            !InstallationTrust.IsInstalledPayload(out _))
        {
            return;
        }

        var completedVersion = ReadCompletedVersion();
        if (completedVersion >= CurrentCleanupVersion) return;

        InstallResidueCleanupResult result;
        try
        {
            result = CleanupVersions(
                completedVersion,
                InstallationTrust.ExpectedInstallationDirectory,
                LegacyProgramDataRoot());
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            Console.WriteLine(
                "Legacy Vita Moonlight cleanup was safely deferred: " +
                error.Message);
            return;
        }

        if (!result.Completed)
        {
            Console.WriteLine(
                "Legacy Vita Moonlight cleanup retained " +
                $"{result.RetainedOwnedEntries.Count} busy or untrusted " +
                "owned entry/entries and will retry later.");
            return;
        }

        WriteCompletedVersion(CurrentCleanupVersion);
        if (result.RemovedFiles > 0 || result.RemovedDirectories > 0)
        {
            Console.WriteLine(
                $"Removed {result.RemovedFiles} obsolete Vita Moonlight " +
                $"file(s) and {result.RemovedDirectories} empty legacy " +
                "directory/directories.");
        }
    }

    internal static InstallResidueCleanupResult
        CleanupObsoleteRootPayloadForUninstall(string installRoot)
    {
        var accumulator = new CleanupAccumulator();
        CleanupObsoleteRootPayloadV1(installRoot, accumulator);
        return accumulator.ToResult();
    }

    internal static InstallResidueCleanupResult
        CleanupLegacyProgramDataForUninstall(string legacyProgramDataRoot)
    {
        var accumulator = new CleanupAccumulator();
        CleanupLegacyProgramDataV1(
            legacyProgramDataRoot,
            accumulator);
        return accumulator.ToResult();
    }

    /// <summary>
    /// Test seam and version dispatcher. It never enumerates recursively and
    /// accepts only fixed leaf-name arrays compiled into this assembly.
    /// </summary>
    internal static InstallResidueCleanupResult CleanupVersions(
        int completedVersion,
        string installRoot,
        string legacyProgramDataRoot)
    {
        if (completedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedVersion));
        }

        var accumulator = new CleanupAccumulator();
        if (completedVersion < 1)
        {
            CleanupObsoleteRootPayloadV1(installRoot, accumulator);
            CleanupLegacyProgramDataV1(
                legacyProgramDataRoot,
                accumulator);
        }

        return accumulator.ToResult();
    }

    private static void CleanupObsoleteRootPayloadV1(
        string installRoot,
        CleanupAccumulator accumulator) =>
        CleanupKnownDirectory(
            installRoot,
            ObsoleteRootPayloadFilesV1,
            removeIfEmpty: false,
            accumulator);

    private static void CleanupLegacyProgramDataV1(
        string legacyProgramDataRoot,
        CleanupAccumulator accumulator)
    {
        CleanupKnownDirectory(
            Path.Combine(legacyProgramDataRoot, "Diagnostics"),
            LegacyProgramDataDiagnosticsFilesV1,
            removeIfEmpty: true,
            accumulator,
            pinnedParentPath: legacyProgramDataRoot);
        CleanupKnownDirectory(
            legacyProgramDataRoot,
            LegacyProgramDataStateFilesV1,
            removeIfEmpty: true,
            accumulator);
    }

    private static void CleanupKnownDirectory(
        string directoryPath,
        IReadOnlyList<string> ownedFileNames,
        bool removeIfEmpty,
        CleanupAccumulator accumulator,
        string? pinnedParentPath = null)
    {
        var fullPath = NormalizeNonRootDirectory(directoryPath);
        ValidateExactLeafNames(ownedFileNames);

        if (!OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(fullPath)) return;
            foreach (var fileName in ownedFileNames)
            {
                var candidate = Path.Combine(fullPath, fileName);
                try
                {
                    if (TrustedFileSystem.DeleteFile(candidate))
                    {
                        accumulator.RemovedFiles++;
                    }
                }
                catch (Exception error) when (
                    error is IOException or UnauthorizedAccessException)
                {
                    accumulator.RetainedOwnedEntries.Add(candidate);
                }
            }
            TryRemoveEmptyDirectory(
                fullPath,
                removeIfEmpty,
                expectedIdentity: null,
                observedEmpty: !Directory.EnumerateFileSystemEntries(fullPath)
                    .Any(),
                accumulator);
            return;
        }

        TrustedFileIdentity? identity = null;
        bool? observedEmpty = null;
        try
        {
            using var parent = pinnedParentPath is null
                ? null
                : AcquirePinnedParent(fullPath, pinnedParentPath);
            using var directory =
                TrustedFileSystem.AcquireDirectoryLease(fullPath);
            identity = directory.Identity;
            foreach (var fileName in ownedFileNames)
            {
                var candidate = Path.Combine(fullPath, fileName);
                try
                {
                    if (TrustedFileSystem.DeleteFile(candidate))
                    {
                        accumulator.RemovedFiles++;
                    }
                }
                catch (Exception error) when (
                    error is IOException or
                        UnauthorizedAccessException or
                        InvalidDataException or
                        System.ComponentModel.Win32Exception)
                {
                    accumulator.RetainedOwnedEntries.Add(candidate);
                }
            }
            if (removeIfEmpty)
            {
                // The path cannot be renamed while this no-follow handle is
                // open without delete sharing, so enumeration cannot escape
                // through a substituted parent or directory reparse point.
                observedEmpty =
                    !Directory.EnumerateFileSystemEntries(fullPath).Any();
            }
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            // A reparse point, wrong object type, or inaccessible directory is
            // never followed. Keep the fixed owned names pending for a later
            // safe retry rather than marking this cleanup generation done.
            accumulator.RetainedOwnedEntries.AddRange(
                ownedFileNames.Select(fileName =>
                    Path.Combine(fullPath, fileName)));
            return;
        }

        TryRemoveEmptyDirectory(
            fullPath,
            removeIfEmpty,
            identity,
            observedEmpty,
            accumulator);
    }

    private static TrustedDirectoryLease AcquirePinnedParent(
        string childPath,
        string parentPath)
    {
        var parent = NormalizeNonRootDirectory(parentPath);
        var relative = Path.GetRelativePath(parent, childPath);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal) ||
            relative.IndexOfAny(
                [Path.DirectorySeparatorChar,
                 Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException(
                $"Cleanup child {childPath} is not an exact child of {parent}.");
        }
        return TrustedFileSystem.AcquireDirectoryLease(parent);
    }

    private static void TryRemoveEmptyDirectory(
        string fullPath,
        bool removeIfEmpty,
        TrustedFileIdentity? expectedIdentity,
        bool? observedEmpty,
        CleanupAccumulator accumulator)
    {
        if (!removeIfEmpty) return;
        if (observedEmpty != true)
        {
            accumulator.RetainedDirectories.Add(fullPath);
            return;
        }
        try
        {
            var removed = OperatingSystem.IsWindows()
                ? TrustedFileSystem.DeleteEmptyDirectory(
                    fullPath,
                    expectedIdentity ?? throw new InvalidDataException(
                        $"The directory identity is unavailable: {fullPath}"))
                : DeleteEmptyDirectoryPortable(fullPath);
            if (removed)
            {
                accumulator.RemovedDirectories++;
            }
            else
            {
                // The directory was proven empty under its identity lease but
                // changed before the exact no-follow delete. Retry this
                // generation once the competing writer is gone.
                accumulator.RetainedOwnedEntries.Add(fullPath);
                accumulator.RetainedDirectories.Add(fullPath);
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Another idempotent cleanup completed first.
        }
        catch (Exception error) when (
            error is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception)
        {
            accumulator.RetainedOwnedEntries.Add(fullPath);
            accumulator.RetainedDirectories.Add(fullPath);
        }
    }

    private static bool DeleteEmptyDirectoryPortable(string path)
    {
        if (!Directory.Exists(path)) return false;
        Directory.Delete(path, recursive: false);
        return true;
    }

    private static void ValidateExactLeafNames(
        IEnumerable<string> fileNames)
    {
        foreach (var fileName in fileNames)
        {
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName is "." or ".." ||
                !string.Equals(
                    fileName,
                    Path.GetFileName(fileName),
                    StringComparison.Ordinal) ||
                fileName.IndexOfAny(
                    [Path.DirectorySeparatorChar,
                     Path.AltDirectorySeparatorChar]) >= 0)
            {
                throw new InvalidDataException(
                    $"Cleanup allowlists may contain only exact leaf names: {fileName}");
            }
        }
    }

    private static string NormalizeNonRootDirectory(string directoryPath)
    {
        var absolute = Path.GetFullPath(directoryPath);
        var normalized = absolute.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(absolute)?.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalized) ||
            string.Equals(
                normalized,
                volumeRoot,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Refusing cleanup against a filesystem root: {directoryPath}");
        }
        return normalized;
    }

    private static int ReadCompletedVersion()
    {
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.OpenSubKey(RegistryPath);
        return key?.GetValue(CleanupVersionValue) is int version
            ? version
            : 0;
    }

    private static void WriteCompletedVersion(int version)
    {
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.CreateSubKey(
            RegistryPath,
            writable: true) ?? throw new UnauthorizedAccessException(
            "Windows could not record the completed legacy cleanup version.");
        key.SetValue(
            CleanupVersionValue,
            version,
            RegistryValueKind.DWord);
    }

    private static string LegacyProgramDataRoot() => Path.Combine(
        Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData),
        "VitaMoonlight");

    private sealed class CleanupAccumulator
    {
        internal int RemovedFiles { get; set; }
        internal int RemovedDirectories { get; set; }
        internal List<string> RetainedOwnedEntries { get; } = [];
        internal List<string> RetainedDirectories { get; } = [];

        internal InstallResidueCleanupResult ToResult() => new(
            RemovedFiles,
            RemovedDirectories,
            RetainedOwnedEntries,
            RetainedDirectories);
    }
}
