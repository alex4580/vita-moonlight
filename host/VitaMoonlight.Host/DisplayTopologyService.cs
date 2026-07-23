using System.Runtime.InteropServices;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DisplayDescriptor(
    int PathIndex,
    string FriendlyName,
    string DevicePath,
    bool IsActive,
    bool IsAvailable,
    int OutputTechnology = -1);

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
    internal static string RescueStatusFile => Path.Combine(Root, "stream-rescue-status.json");
    internal static string RescueLogFile => Path.Combine(Root, "stream-rescue.log");
    internal static string LastErrorFile => Path.Combine(Root, "last-command-error.txt");
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
                path.TargetInfo.TargetAvailable != 0,
                path.TargetInfo.OutputTechnology);
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

    internal IReadOnlyList<string> RecoverPhysicalDisplays()
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryAllPaths);
        var displays = Describe(configuration);
        var selected = SelectPhysicalDisplaysForRecovery(displays);
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("No available physical display was found for emergency recovery.");
        }

        var indexes = selected.Select(display => display.PathIndex).ToHashSet();
        WindowsDisplayNative.ApplyPaths(configuration.Paths
            .Where((_, index) => indexes.Contains(index))
            .ToArray());
        return selected
            .Select(display => string.IsNullOrWhiteSpace(display.FriendlyName) ? display.DevicePath : display.FriendlyName)
            .ToArray();
    }

    internal static DisplayDescriptor[] SelectPhysicalDisplaysForRecovery(IEnumerable<DisplayDescriptor> displays)
    {
        var availablePhysical = displays
            .Where(display => display.IsAvailable && !IsLikelyVirtualDisplay(display))
            .ToArray();
        var activePhysical = availablePhysical.Where(display => display.IsActive).ToArray();
        return activePhysical.Length > 0 ? activePhysical : availablePhysical;
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

    internal DisplayDescriptor ActivateVirtualDisplay(
        string? nameMatch,
        int width,
        int height,
        int fps,
        bool forceSdr = true,
        bool persistMode = false)
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryAllPaths);
        var displays = Describe(configuration);
        var selected = SelectVirtualDisplayForActivation(displays, nameMatch);

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
        WindowsDisplayNative.ChangeSourceMode(sourceName.ViewGdiDeviceName, width, height, fps, persistMode);
        if (forceSdr)
        {
            WindowsDisplayNative.TrySetAdvancedColorState(activePath.Value, false);
        }
        return selected;
    }

    internal static DisplayDescriptor? SelectVirtualDisplayForActivation(
        IEnumerable<DisplayDescriptor> displays,
        string? nameMatch)
    {
        var candidates = displays
            .Where(display => display.IsAvailable && IsLikelyVirtualDisplay(display))
            .ToArray();
        if (!string.IsNullOrWhiteSpace(nameMatch))
        {
            return candidates.FirstOrDefault(display =>
                display.FriendlyName.Contains(nameMatch, StringComparison.OrdinalIgnoreCase) ||
                display.DevicePath.Contains(nameMatch, StringComparison.OrdinalIgnoreCase));
        }
        return candidates.FirstOrDefault(IsManagedVirtualDisplay) ?? candidates.FirstOrDefault();
    }

    internal DisplayDescriptor ChangeActiveVirtualDisplayMode(
        string? nameMatch,
        int width,
        int height,
        int fps,
        bool forceSdr = true)
    {
        var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
        var candidates = Describe(configuration)
            .Where(display => display.IsActive && IsLikelyVirtualDisplay(display))
            .ToArray();
        var selected = !string.IsNullOrWhiteSpace(nameMatch)
            ? candidates.FirstOrDefault(display =>
                display.FriendlyName.Contains(nameMatch, StringComparison.OrdinalIgnoreCase) ||
                display.DevicePath.Contains(nameMatch, StringComparison.OrdinalIgnoreCase))
            : candidates.FirstOrDefault(IsManagedVirtualDisplay) ?? candidates.FirstOrDefault();
        if (selected is null)
        {
            throw new InvalidOperationException(
                "No active Vita virtual display was found. Start a Vita stream before changing its desktop mode.");
        }

        var path = configuration.Paths[selected.PathIndex];
        var sourceName = WindowsDisplayNative.GetSourceNameFor(path);
        WindowsDisplayNative.ChangeSourceMode(sourceName.ViewGdiDeviceName, width, height, fps);
        if (forceSdr)
        {
            WindowsDisplayNative.TrySetAdvancedColorState(path, false);
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

    internal static bool IsLikelyVirtualDisplay(DisplayDescriptor display)
    {
        var identity = $"{display.FriendlyName} {display.DevicePath}";
        return display.OutputTechnology is
                   WindowsDisplayNative.OutputTechnologyIndirectWired or
                   WindowsDisplayNative.OutputTechnologyIndirectVirtual ||
               IsManagedVirtualDisplay(display) ||
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
