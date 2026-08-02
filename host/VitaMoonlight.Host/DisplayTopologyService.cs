using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VitaMoonlight.Host;

internal sealed record DisplayDescriptor(
    int PathIndex,
    string FriendlyName,
    string DevicePath,
    bool IsActive,
    bool IsAvailable,
    int OutputTechnology = -1,
    int? Width = null,
    int? Height = null,
    int? RefreshRate = null);

internal sealed record PhysicalDisplayModeRepairWarning(
    string Code,
    string Display,
    string Detail)
{
    public override string ToString() =>
        $"{Code} ({Display}): {Detail}";
}

internal sealed record PhysicalDisplayModeRepairResult(
    IReadOnlyList<string> RestoredModes,
    IReadOnlyList<PhysicalDisplayModeRepairWarning> Warnings)
{
    internal static PhysicalDisplayModeRepairResult Empty { get; } =
        new([], []);
}

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
            return Path.Combine(
                InstallationTrust.ExpectedInstallationDirectory,
                "state");
        }
    }

    internal static string RecoveryFile => Path.Combine(Root, "display-recovery.json");
    internal static string SettingsFile => Path.Combine(Root, "host-settings.json");
    internal static string LockFile => Path.Combine(Root, "session.lock");
    internal static string DiagnosticsDirectory => Path.Combine(Root, "Diagnostics");
    internal static string RescueStatusFile => Path.Combine(
        DiagnosticsDirectory,
        "stream-rescue-status.json");
    internal static string RescueLogFile => Path.Combine(
        DiagnosticsDirectory,
        "stream-rescue.log");
    internal static string LastErrorFile => Path.Combine(Root, "last-command-error.txt");
}

internal sealed class DisplayTopologyService
{
    private sealed record VirtualDisplaySelection(
        DisplayConfiguration Configuration,
        DisplayDescriptor Display);

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

            AdvertisedDisplayMode? activeMode = null;
            if ((path.Flags & WindowsDisplayNative.PathActive) != 0)
            {
                try
                {
                    var source = WindowsDisplayNative.GetSourceNameFor(path);
                    activeMode = WindowsDisplayNative.ReadCurrentSourceMode(
                        source.ViewGdiDeviceName);
                }
                catch (Exception error) when (
                    error is InvalidOperationException or Win32Exception)
                {
                    // Keep topology inventory available while Windows finishes
                    // enumerating a source mode. Resume recovery will retry.
                }
            }

            var descriptor = new DisplayDescriptor(
                index,
                name.MonitorFriendlyDeviceName ?? string.Empty,
                name.MonitorDevicePath ?? string.Empty,
                (path.Flags & WindowsDisplayNative.PathActive) != 0,
                path.TargetInfo.TargetAvailable != 0,
                path.TargetInfo.OutputTechnology,
                activeMode?.Width,
                activeMode?.Height,
                activeMode?.Fps);
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

    internal IReadOnlyList<string> RecoverPhysicalDisplays() =>
        RecoverPhysicalDisplays(out _);

    internal IReadOnlyList<string> RecoverPhysicalDisplays(
        out PhysicalDisplayModeRepairResult modeRepair)
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
        // Mode repair is deliberately advisory. Activating a visible physical
        // path is the safety invariant; a monitor with an unusual registry or
        // mode-enumeration implementation must not turn that success into a
        // failed Pause, uninstall, or emergency recovery.
        try
        {
            modeRepair = RestorePersistedPhysicalDisplayModes();
        }
        catch (Exception error)
        {
            // Keep a successfully activated physical topology successful even
            // if a future/native mode-repair implementation introduces an
            // exception that the best-effort routine did not anticipate.
            modeRepair = new PhysicalDisplayModeRepairResult(
                [],
                [new PhysicalDisplayModeRepairWarning(
                    "mode-repair-unexpected",
                    "physical displays",
                    FormatModeRepairError(error))]);
        }
        return selected
            .Select(display => string.IsNullOrWhiteSpace(display.FriendlyName) ? display.DevicePath : display.FriendlyName)
            .ToArray();
    }

    /// <summary>
    /// Repairs the sleep/resume failure where Windows activates the physical
    /// monitor at a temporary Vita/800x600 fallback mode. The persisted user
    /// mode is read from Windows for the same active physical source and is
    /// applied only when the current mode is an unambiguous Vita/800x600
    /// fallback (or a 30 Hz form of the persisted resolution) and that exact
    /// source advertises the persisted resolution. Per-display failures are
    /// returned as structured warnings and never invalidate a visible physical
    /// topology. No virtual display or disconnected/docked-away target is
    /// changed.
    /// </summary>
    internal PhysicalDisplayModeRepairResult
        RestorePersistedPhysicalDisplayModes()
    {
        DisplayConfiguration configuration;
        IReadOnlyList<DisplayDescriptor> displays;
        try
        {
            configuration = WindowsDisplayNative.Query(
                WindowsDisplayNative.QueryOnlyActivePaths);
            displays = Describe(configuration);
        }
        catch (Exception error)
        {
            return new PhysicalDisplayModeRepairResult(
                [],
                [new PhysicalDisplayModeRepairWarning(
                    "mode-inventory-unavailable",
                    "physical displays",
                    FormatModeRepairError(error))]);
        }

        var restored = new List<string>();
        var warnings = new List<PhysicalDisplayModeRepairWarning>();
        foreach (var display in displays.Where(display =>
                     display.IsActive &&
                     display.IsAvailable &&
                     !IsLikelyVirtualDisplay(display)))
        {
            var displayLabel = DisplayLabel(display);
            try
            {
                var path = configuration.Paths[display.PathIndex];
                var source = WindowsDisplayNative.GetSourceNameFor(path);
                var current = WindowsDisplayNative.ReadCurrentSourceMode(
                    source.ViewGdiDeviceName);
                var persisted = WindowsDisplayNative.ReadPersistedSourceMode(
                    source.ViewGdiDeviceName);
                if (!IsClearPhysicalModeDrift(current, persisted))
                {
                    continue;
                }

                var advertised = WindowsDisplayNative.EnumerateSourceModes(
                    source.ViewGdiDeviceName);
                var matchingResolution = advertised.Where(mode =>
                    mode.Width == persisted.Width &&
                    mode.Height == persisted.Height).ToArray();
                if (matchingResolution.Length == 0)
                {
                    warnings.Add(new PhysicalDisplayModeRepairWarning(
                        "persisted-mode-not-advertised",
                        displayLabel,
                        $"current={current}; persisted={persisted}"));
                    continue;
                }

                var resolutionChanged =
                    current.Width != persisted.Width ||
                    current.Height != persisted.Height;
                var target = matchingResolution
                        .Where(mode =>
                            persisted.Fps > 1 && mode.Fps == persisted.Fps)
                        .Select(mode => (AdvertisedDisplayMode?)mode)
                        .FirstOrDefault()
                    ?? (resolutionChanged
                        ? matchingResolution
                            .Where(mode => mode.Fps == current.Fps)
                            .Select(mode => (AdvertisedDisplayMode?)mode)
                            .FirstOrDefault()
                        : null)
                    ?? matchingResolution
                        .OrderBy(mode => persisted.Fps > 1
                            ? Math.Abs(mode.Fps - persisted.Fps)
                            : 0)
                        .ThenByDescending(mode => mode.Fps)
                        .First();
                if (current == target)
                {
                    continue;
                }

                WindowsDisplayNative.ChangeSourceMode(
                    source.ViewGdiDeviceName,
                    target.Width,
                    target.Height,
                    target.Fps);
                restored.Add($"{displayLabel} {current} -> {target}");
            }
            catch (Exception error)
            {
                warnings.Add(new PhysicalDisplayModeRepairWarning(
                    "mode-repair-failed",
                    displayLabel,
                    FormatModeRepairError(error)));
            }
        }
        return new PhysicalDisplayModeRepairResult(restored, warnings);
    }

    internal static bool IsClearPhysicalModeDrift(
        AdvertisedDisplayMode current,
        AdvertisedDisplayMode persisted)
    {
        if (current == persisted ||
            persisted.Width < 640 ||
            persisted.Height < 480)
        {
            return false;
        }

        var resolutionDrift =
            IsKnownFallbackResolution(current) &&
            !IsKnownFallbackResolution(persisted) &&
            (long)persisted.Width * persisted.Height >
            (long)current.Width * current.Height;
        var refreshDrift =
            current.Width == persisted.Width &&
            current.Height == persisted.Height &&
            current.Fps is > 1 and <= 30 &&
            persisted.Fps >= 50;
        return resolutionDrift || refreshDrift;
    }

    private static bool IsKnownFallbackResolution(
        AdvertisedDisplayMode mode) =>
        (mode.Width, mode.Height) is
            (800, 600) or
            (960, 540) or
            (960, 544);

    private static string DisplayLabel(DisplayDescriptor display) =>
        string.IsNullOrWhiteSpace(display.FriendlyName)
            ? $"physical display {display.PathIndex + 1}"
            : display.FriendlyName;

    private static string FormatModeRepairError(Exception error) =>
        $"{error.GetType().Name}:0x{error.HResult:X8}";

    internal static DisplayDescriptor[] SelectPhysicalDisplaysForRecovery(IEnumerable<DisplayDescriptor> displays)
    {
        var availablePhysical = displays
            .Where(display => display.IsAvailable && !IsLikelyVirtualDisplay(display))
            .ToArray();
        var activePhysical = availablePhysical.Where(display => display.IsActive).ToArray();
        return activePhysical.Length > 0 ? activePhysical : availablePhysical;
    }

    internal void SaveRecovery(
        DisplayTransactionLease transaction,
        DisplayRecoveryRecord recovery)
    {
        transaction.RequireActive();
        TrustedFileSystem.WriteAllText(
            HostStatePaths.RecoveryFile,
            JsonSerializer.Serialize(recovery, JsonOptions));
    }

    internal DisplayRecoveryRecord LoadRecovery()
    {
        var recovery = JsonSerializer.Deserialize<DisplayRecoveryRecord>(
            TrustedFileSystem.ReadAllText(HostStatePaths.RecoveryFile),
            JsonOptions)
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

    internal DisplayDescriptor VerifyVirtualDisplayModeSafely(
        string? nameMatch,
        int width,
        int height,
        int fps,
        bool forceSdr = true,
        bool persistMode = true,
        int modeAttempts = 40,
        int enumerationAttempts = 60)
    {
        if (modeAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(modeAttempts));
        }
        if (enumerationAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(enumerationAttempts));
        }

        // Updating an already-installed IDD restarts its device stack. Windows
        // can report a successful PnP restart several seconds before the
        // display target returns to QueryDisplayConfig. Do not mistake that
        // transient gap for a bad installation, and do not alter the active
        // topology while waiting for the target to finish enumerating.
        var availableSelection = WaitForVirtualDisplay(nameMatch, enumerationAttempts);
        var availableConfiguration = availableSelection.Configuration;
        var selected = availableSelection.Display;

        var activeConfiguration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
        var physicalDisplays = SelectActivePhysicalDisplaysForVerification(Describe(activeConfiguration));
        if (physicalDisplays.Length == 0)
        {
            throw new InvalidOperationException(
                "No active physical display was found. Refusing to verify the virtual display without a visible recovery screen.");
        }

        var physicalIndexes = physicalDisplays.Select(display => display.PathIndex).ToHashSet();
        var verificationPaths = activeConfiguration.Paths
            .Where((_, index) => physicalIndexes.Contains(index))
            .Append(availableConfiguration.Paths[selected.PathIndex])
            .ToArray();

        // Verification must never make the VDD the machine's only active
        // display. Keep every active physical path in the supplied topology,
        // add the VDD as an extended display, and let SessionManager restore
        // the exact original topology after the native mode is confirmed.
        WindowsDisplayNative.ApplyPaths(verificationPaths, forceModeEnumeration: true);

        Exception? lastError = null;
        for (var attempt = 0; attempt < modeAttempts; attempt++)
        {
            try
            {
                var currentConfiguration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryOnlyActivePaths);
                var activePath = FindPathByDevicePath(currentConfiguration, selected.DevicePath)
                    ?? throw new InvalidOperationException(
                        "The selected virtual display did not become active beside the physical display.");
                var sourceName = WindowsDisplayNative.GetSourceNameFor(activePath);
                WindowsDisplayNative.ChangeSourceMode(
                    sourceName.ViewGdiDeviceName,
                    width,
                    height,
                    fps,
                    persistMode);
                if (forceSdr)
                {
                    WindowsDisplayNative.TrySetAdvancedColorState(activePath, false);
                }
                return selected;
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                lastError = error;
                if (attempt < modeAttempts - 1)
                {
                    Thread.Sleep(500);
                }
            }
        }

        throw new InvalidOperationException(
            $"Windows did not accept {width}x{height}@{fps} for the virtual display within " +
            $"{modeAttempts * 0.5:0.#} seconds. The original display layout will be restored. " +
            $"Last Windows response: {lastError?.Message ?? "no mode response was returned"}.",
            lastError);
    }

    private static VirtualDisplaySelection WaitForVirtualDisplay(
        string? nameMatch,
        int attempts)
    {
        Exception? lastError = null;
        IReadOnlyList<DisplayDescriptor> lastDisplays = Array.Empty<DisplayDescriptor>();
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                var configuration = WindowsDisplayNative.Query(WindowsDisplayNative.QueryAllPaths);
                lastDisplays = Describe(configuration);
                var selected = SelectVirtualDisplayForActivation(lastDisplays, nameMatch);
                if (selected is not null)
                {
                    return new VirtualDisplaySelection(configuration, selected);
                }
            }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception)
            {
                lastError = error;
            }

            if (attempt < attempts - 1)
            {
                Thread.Sleep(500);
            }
        }

        var availableVirtualDisplays = lastDisplays
            .Where(display => display.IsAvailable && IsLikelyVirtualDisplay(display))
            .Select(DisplayIdentity)
            .ToArray();
        var detail = availableVirtualDisplays.Length == 0
            ? "Windows did not enumerate an available virtual display target."
            : string.IsNullOrWhiteSpace(nameMatch)
                ? $"Windows enumerated: {string.Join(", ", availableVirtualDisplays)}."
                : $"Windows enumerated {string.Join(", ", availableVirtualDisplays)}, but none matched '{nameMatch}'.";
        var response = lastError is null
            ? string.Empty
            : $" Last Windows response: {lastError.Message}.";
        throw new InvalidOperationException(
            $"The virtual display did not become available within {attempts * 0.5:0.#} seconds after its driver restart. " +
            $"{detail}{response} Keep the physical display enabled, wait a few seconds, then click " +
            "Repair Vita display driver again. If more than one virtual display is listed, select the intended one under Streaming.");
    }

    private static string DisplayIdentity(DisplayDescriptor display) =>
        string.IsNullOrWhiteSpace(display.FriendlyName)
            ? display.DevicePath
            : display.FriendlyName;

    private static DisplayPathInfo? FindPathByDevicePath(
        DisplayConfiguration configuration,
        string devicePath)
    {
        foreach (var candidate in configuration.Paths)
        {
            try
            {
                var targetName = WindowsDisplayNative.GetTargetNameFor(candidate);
                if (string.Equals(
                    targetName.MonitorDevicePath,
                    devicePath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch
            {
                // A transient/unnamed target is not the selected display.
            }
        }
        return null;
    }

    internal static DisplayDescriptor[] SelectActivePhysicalDisplaysForVerification(
        IEnumerable<DisplayDescriptor> displays) =>
        displays
            .Where(display =>
                display.IsActive &&
                display.IsAvailable &&
                !IsLikelyVirtualDisplay(display))
            .ToArray();

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

    internal static bool ClearRecovery(
        DisplayTransactionLease transaction)
    {
        transaction.RequireActive();
        return TrustedFileSystem.DeleteFile(HostStatePaths.RecoveryFile);
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
