using System.Runtime.InteropServices;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DisplayDescriptor(
    int PathIndex,
    string FriendlyName,
    string DevicePath,
    bool IsActive,
    bool IsAvailable);

internal sealed record DisplayRecoveryRecord(
    int FormatVersion,
    DateTimeOffset CapturedAt,
    int PathStructureSize,
    int ModeStructureSize,
    int PathCount,
    int ModeCount,
    string PathsBase64,
    string ModesBase64,
    string? SelectedDisplay,
    int RequestedWidth,
    int RequestedHeight,
    int RequestedFps);

internal static class HostStatePaths
{
    internal static string Root
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("VITA_MOONLIGHT_STATE_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetFullPath(configured);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VitaMoonlight");
        }
    }

    internal static string RecoveryFile => Path.Combine(Root, "display-recovery.json");
    internal static string SettingsFile => Path.Combine(Root, "host-settings.json");
    internal static string LockFile => Path.Combine(Root, "session.lock");
}

internal sealed class DisplayTopologyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal IReadOnlyList<DisplayDescriptor> ListDisplays()
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryAllPaths);
        return Describe(configuration);
    }

    private static IReadOnlyList<DisplayDescriptor> Describe(DisplayConfiguration configuration)
    {
        var displays = new Dictionary<string, DisplayDescriptor>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < configuration.Paths.Length; index++)
        {
            var path = configuration.Paths[index];
            DisplayTargetName name;
            try
            {
                name = WindowsDisplayNative.GetTargetNameFor(path);
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name.MonitorFriendlyDeviceName) && string.IsNullOrWhiteSpace(name.MonitorDevicePath))
            {
                continue;
            }

            var descriptor = new DisplayDescriptor(
                index,
                name.MonitorFriendlyDeviceName ?? string.Empty,
                name.MonitorDevicePath ?? string.Empty,
                (path.Flags & WindowsDisplayNative.PathActive) != 0,
                path.TargetInfo.TargetAvailable != 0);
            var key = string.IsNullOrWhiteSpace(descriptor.DevicePath)
                ? $"{path.TargetInfo.AdapterId.HighPart}:{path.TargetInfo.AdapterId.LowPart}:{path.TargetInfo.Id}"
                : descriptor.DevicePath;
            if (!displays.TryGetValue(key, out var existing) || (!existing.IsActive && descriptor.IsActive))
            {
                displays[key] = descriptor;
            }
        }
        return displays.Values.OrderByDescending(display => display.IsActive).ThenBy(display => display.FriendlyName).ToArray();
    }

    internal DisplayRecoveryRecord CaptureRecovery(int width, int height, int fps)
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
        return new DisplayRecoveryRecord(
            1,
            DateTimeOffset.UtcNow,
            Marshal.SizeOf<DisplayPathInfo>(),
            Marshal.SizeOf<DisplayModeInfo>(),
            configuration.Paths.Length,
            configuration.Modes.Length,
            Convert.ToBase64String(WindowsDisplayNative.StructuresToBytes(configuration.Paths)),
            Convert.ToBase64String(WindowsDisplayNative.StructuresToBytes(configuration.Modes)),
            null,
            width,
            height,
            fps);
    }

    internal bool DisableManagedVirtualDisplays()
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
        var managedIndexes = Describe(configuration)
            .Where(display => display.IsActive && IsManagedVirtualDisplay(display))
            .Select(display => display.PathIndex)
            .ToHashSet();
        if (managedIndexes.Count == 0)
        {
            return false;
        }

        var remainingPaths = configuration.Paths
            .Where((_, index) => !managedIndexes.Contains(index))
            .ToArray();
        if (remainingPaths.Length == 0)
        {
            throw new InvalidOperationException(
                "The Vita virtual display is the only active display. Refusing to disable the machine's last screen.");
        }

        WindowsDisplayNative.ApplyPaths(remainingPaths);
        return true;
    }

    internal void SaveRecovery(DisplayRecoveryRecord recovery)
    {
        Directory.CreateDirectory(HostStatePaths.Root);
        AtomicWrite(HostStatePaths.RecoveryFile, JsonSerializer.Serialize(recovery, JsonOptions));
    }

    internal DisplayRecoveryRecord LoadRecovery()
    {
        var recovery = JsonSerializer.Deserialize<DisplayRecoveryRecord>(File.ReadAllText(HostStatePaths.RecoveryFile), JsonOptions)
            ?? throw new InvalidDataException("The display recovery record is empty.");
        if (recovery.FormatVersion != 1 ||
            recovery.PathStructureSize != Marshal.SizeOf<DisplayPathInfo>() ||
            recovery.ModeStructureSize != Marshal.SizeOf<DisplayModeInfo>())
        {
            throw new InvalidDataException("The display recovery record is incompatible with this host companion version.");
        }
        return recovery;
    }

    internal DisplayDescriptor ActivateVirtualDisplay(string? nameMatch, int width, int height, int fps, bool forceSdr = true)
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryAllPaths);
        var displays = Describe(configuration);
        var candidates = displays.Where(display => display.IsAvailable).ToList();
        DisplayDescriptor? selected;
        if (!string.IsNullOrWhiteSpace(nameMatch))
        {
            selected = candidates.FirstOrDefault(display =>
                display.FriendlyName.Contains(nameMatch, StringComparison.OrdinalIgnoreCase) ||
                display.DevicePath.Contains(nameMatch, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            selected = candidates.FirstOrDefault(IsLikelyVirtualDisplay);
        }

        if (selected is null)
        {
            throw new InvalidOperationException(
                "No virtual display was found. Run `display list` and configure its name with `configure --display-match <text>`."
            );
        }

        var path = configuration.Paths[selected.PathIndex];
        WindowsDisplayNative.ValidateSinglePath(path);
        WindowsDisplayNative.ApplySinglePath(path);
        var activeConfiguration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
        DisplayPathInfo? activePath = null;
        foreach (var candidate in activeConfiguration.Paths)
        {
            try
            {
                var targetName = WindowsDisplayNative.GetTargetNameFor(candidate);
                if (string.Equals(targetName.MonitorDevicePath, selected.DevicePath, StringComparison.OrdinalIgnoreCase))
                {
                    activePath = candidate;
                    break;
                }
            }
            catch
            {
                // A transient/unnamed target is not the display selected above.
            }
        }
        if (activePath is null)
        {
            throw new InvalidOperationException("The selected virtual display did not become active.");
        }
        var sourceName = WindowsDisplayNative.GetSourceNameFor(activePath.Value);
        WindowsDisplayNative.ChangeSourceMode(sourceName.ViewGdiDeviceName, width, height, fps);
        if (forceSdr)
        {
            WindowsDisplayNative.TrySetAdvancedColorState(activePath.Value, false);
        }
        return selected;
    }

    internal void Restore()
    {
        var recovery = LoadRecovery();
        var paths = WindowsDisplayNative.BytesToStructures<DisplayPathInfo>(Convert.FromBase64String(recovery.PathsBase64), recovery.PathCount);
        var modes = WindowsDisplayNative.BytesToStructures<DisplayModeInfo>(Convert.FromBase64String(recovery.ModesBase64), recovery.ModeCount);
        WindowsDisplayNative.Restore(new DisplayConfiguration(paths, modes));
    }

    internal static void ClearRecovery()
    {
        if (File.Exists(HostStatePaths.RecoveryFile))
        {
            File.Delete(HostStatePaths.RecoveryFile);
        }
    }

    internal static void AtomicWrite(string path, string content)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("The destination has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }

    private static bool IsLikelyVirtualDisplay(DisplayDescriptor display)
    {
        var identity = $"{display.FriendlyName} {display.DevicePath}";
        return IsManagedVirtualDisplay(display) ||
               identity.Contains("virtual", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("iddsample", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("idd", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsManagedVirtualDisplay(DisplayDescriptor display)
    {
        var identity = $"{display.FriendlyName} {display.DevicePath}";
        return identity.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("VDD by MTT", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("MTT1337", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("MttVDD", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("IddSampleDriver", StringComparison.OrdinalIgnoreCase);
    }
}
