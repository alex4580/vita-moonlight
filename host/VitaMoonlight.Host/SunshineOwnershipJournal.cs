using System.Text.Json;
using Microsoft.Win32;

namespace VitaMoonlight.Host;

internal sealed record SunshineOwnedValue(
    bool OriginalPresent,
    string? OriginalValue,
    string AppliedValue);

internal sealed record SunshineOwnedHook(
    string ApplicationName,
    string Do,
    string Undo,
    bool Elevated);

internal sealed class SunshineOwnedLocation
{
    public required string ConfigurationDirectory { get; init; }
    public Dictionary<string, SunshineOwnedValue> Values { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<SunshineOwnedHook> Hooks { get; set; } = [];
    public List<string> CreatedApplications { get; set; } = [];
    public bool BackupOwned { get; set; }
    public string? BackupSha256 { get; set; }
}

internal sealed class SunshineOwnershipState
{
    public int FormatVersion { get; init; } = 1;
    public List<SunshineOwnedLocation> Locations { get; init; } = [];
}

internal static class SunshineOwnershipJournal
{
    private const string RegistryPath = @"SOFTWARE\VitaMoonlight\Host";
    private const string RegistryValue = "SunshineOwnership";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static string? testFilePath;

    internal static SunshineOwnershipState Load()
    {
        if (testFilePath is not null)
        {
            return File.Exists(testFilePath)
                ? Deserialize(File.ReadAllText(testFilePath))
                : new SunshineOwnershipState();
        }
        if (!OperatingSystem.IsWindows()) return new SunshineOwnershipState();
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.OpenSubKey(RegistryPath);
        var json = key?.GetValue(RegistryValue) as string;
        if (string.IsNullOrWhiteSpace(json)) return new SunshineOwnershipState();
        return Deserialize(json);
    }

    private static SunshineOwnershipState Deserialize(string json)
    {
        var state = JsonSerializer.Deserialize<SunshineOwnershipState>(json, JsonOptions)
            ?? throw new InvalidDataException("The Sunshine ownership journal is empty.");
        if (state.FormatVersion != 1)
        {
            throw new InvalidDataException(
                $"Unsupported Sunshine ownership journal version {state.FormatVersion}.");
        }
        foreach (var location in state.Locations)
        {
            location.Values = new Dictionary<string, SunshineOwnedValue>(
                location.Values,
                StringComparer.OrdinalIgnoreCase);
        }
        return state;
    }

    internal static void Save(SunshineOwnershipState state)
    {
        if (testFilePath is not null)
        {
            File.WriteAllText(
                testFilePath,
                JsonSerializer.Serialize(state, JsonOptions));
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.CreateSubKey(RegistryPath, writable: true)
            ?? throw new UnauthorizedAccessException(
                "Windows could not create the protected host ownership journal.");
        key.SetValue(
            RegistryValue,
            JsonSerializer.Serialize(state, JsonOptions),
            RegistryValueKind.String);
    }

    internal static void Delete()
    {
        if (testFilePath is not null)
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
            return;
        }
        if (!OperatingSystem.IsWindows()) return;
        using var localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using var key = localMachine.OpenSubKey(
            RegistryPath,
            writable: true);
        key?.DeleteValue(
            RegistryValue,
            throwOnMissingValue: false);
    }

    internal static IDisposable UseTestFile(string path)
    {
        if (testFilePath is not null)
        {
            throw new InvalidOperationException(
                "A Sunshine ownership journal test override is already active.");
        }
        testFilePath = Path.GetFullPath(path);
        return new TestFileScope();
    }

    internal static SunshineOwnedLocation GetOrAddLocation(
        SunshineOwnershipState state,
        string configurationDirectory)
    {
        var normalized = Path.GetFullPath(configurationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = state.Locations.FirstOrDefault(location =>
            string.Equals(
                Path.GetFullPath(location.ConfigurationDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalized,
                StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var created = new SunshineOwnedLocation
        {
            ConfigurationDirectory = normalized,
        };
        state.Locations.Add(created);
        return created;
    }

    internal static string ValidateConfigurationDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException(
                "The recorded Sunshine configuration path has no root.");
        var relative = Path.GetRelativePath(root, fullPath);
        var current = root;
        foreach (var component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current)) continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"Refusing privileged cleanup through reparse directory {current}.");
            }
        }
        return fullPath;
    }

    internal static void ValidateOwnedFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        ValidateConfigurationDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("The owned file has no parent directory."));
        if (File.Exists(fullPath) &&
            (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Refusing privileged cleanup through reparse file {fullPath}.");
        }
    }

    internal static void ValidateTreeNoReparsePoints(string path)
    {
        var fullPath = ValidateConfigurationDirectory(path);
        if (!Directory.Exists(fullPath)) return;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(fullPath));
        var inspected = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (++inspected > 10000)
                {
                    throw new InvalidDataException(
                        $"Refusing to secure unexpectedly large state tree {fullPath}.");
                }
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"Refusing privileged access through reparse entry {entry.FullName}.");
                }
                if (entry is DirectoryInfo child)
                {
                    pending.Push(child);
                }
            }
        }
    }

    private sealed class TestFileScope : IDisposable
    {
        public void Dispose()
        {
            testFilePath = null;
        }
    }
}
