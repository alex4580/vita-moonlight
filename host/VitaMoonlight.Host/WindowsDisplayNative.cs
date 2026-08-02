using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VitaMoonlight.Host;

internal static class WindowsDisplayNative
{
    internal const int OutputTechnologyIndirectWired = 16;
    internal const int OutputTechnologyIndirectVirtual = 17;
    internal const uint QueryAllPaths = 0x00000001;
    internal const uint QueryOnlyActivePaths = 0x00000002;
    internal const uint PathActive = 0x00000001;
    internal const uint PathModeIndexInvalid = 0xFFFFFFFF;

    private const uint SetUseSuppliedDisplayConfig = 0x00000020;
    private const uint SetValidate = 0x00000040;
    private const uint SetApply = 0x00000080;
    private const uint SetAllowChanges = 0x00000400;
    private const uint SetForceModeEnumeration = 0x00001000;
    private const int ErrorInsufficientBuffer = 122;
    private const int GetTargetName = 2;
    private const int GetSourceName = 1;
    private const int SetAdvancedColorState = 10;
    private const int EnumCurrentSettings = -1;
    private const int EnumRegistrySettings = -2;
    private const uint DevModePelsWidth = 0x00080000;
    private const uint DevModePelsHeight = 0x00100000;
    private const uint DevModeDisplayFrequency = 0x00400000;
    private const uint ChangeUpdateRegistry = 0x00000001;

    internal static DisplayConfiguration Query(uint flags)
    {
        EnsureWindows();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            ThrowIfFailed(result, "GetDisplayConfigBufferSizes");

            var paths = new DisplayPathInfo[pathCount];
            var modes = new DisplayModeInfo[modeCount];
            result = QueryDisplayConfig(flags, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }
            ThrowIfFailed(result, "QueryDisplayConfig");
            Array.Resize(ref paths, checked((int)pathCount));
            Array.Resize(ref modes, checked((int)modeCount));
            return new DisplayConfiguration(paths, modes);
        }

        throw new InvalidOperationException("The Windows display topology changed repeatedly while it was being read.");
    }

    internal static DisplayTargetName GetTargetNameFor(DisplayPathInfo path)
    {
        var name = new DisplayTargetName
        {
            Header = new DisplayDeviceInfoHeader
            {
                Type = GetTargetName,
                Size = checked((uint)Marshal.SizeOf<DisplayTargetName>()),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
        };
        var result = DisplayConfigGetDeviceInfo(ref name);
        ThrowIfFailed(result, "DisplayConfigGetDeviceInfo");
        return name;
    }

    internal static DisplaySourceName GetSourceNameFor(DisplayPathInfo path)
    {
        var name = new DisplaySourceName
        {
            Header = new DisplayDeviceInfoHeader
            {
                Type = GetSourceName,
                Size = checked((uint)Marshal.SizeOf<DisplaySourceName>()),
                AdapterId = path.SourceInfo.AdapterId,
                Id = path.SourceInfo.Id,
            },
        };
        var result = DisplayConfigGetDeviceInfo(ref name);
        ThrowIfFailed(result, "DisplayConfigGetDeviceInfo");
        return name;
    }

    internal static void ChangeSourceMode(
        string gdiDeviceName,
        int width,
        int height,
        int fps,
        bool persist = false)
    {
        EnsureWindows();
        var advertisedModes = EnumerateSourceModes(gdiDeviceName);
        if (!advertisedModes.Any(mode =>
            mode.Width == width &&
            mode.Height == height &&
            mode.Fps == fps))
        {
            throw new InvalidOperationException(
                $"{gdiDeviceName} does not advertise {width}x{height}@{fps}. " +
                $"Closest advertised modes: {FormatClosestModes(advertisedModes, width, height, fps)}.");
        }

        var mode = ReadSourceDeviceMode(
            gdiDeviceName,
            EnumCurrentSettings,
            "current");
        if (mode.PelsWidth == width &&
            mode.PelsHeight == height &&
            mode.DisplayFrequency == fps)
        {
            return;
        }
        var currentMode = $"{mode.PelsWidth}x{mode.PelsHeight}@{mode.DisplayFrequency}";
        mode.PelsWidth = checked((uint)width);
        mode.PelsHeight = checked((uint)height);
        mode.DisplayFrequency = checked((uint)fps);
        mode.Fields = DevModePelsWidth | DevModePelsHeight | DevModeDisplayFrequency;
        var result = ChangeDisplaySettingsEx(
            gdiDeviceName,
            ref mode,
            IntPtr.Zero,
            persist ? ChangeUpdateRegistry : 0,
            IntPtr.Zero);
        if (result != 0)
        {
            throw new InvalidOperationException(
                $"Windows rejected {width}x{height}@{fps} for {gdiDeviceName} " +
                $"({DescribeDisplayChangeResult(result)}). " +
                $"Current mode: {currentMode}. " +
                $"Closest advertised modes: {FormatClosestModes(advertisedModes, width, height, fps)}.");
        }

        var applied = ReadSourceDeviceMode(
            gdiDeviceName,
            EnumCurrentSettings,
            "current");
        if (applied.PelsWidth != width || applied.PelsHeight != height || applied.DisplayFrequency != fps)
        {
            throw new InvalidOperationException(
                $"Windows reported success but applied {applied.PelsWidth}x{applied.PelsHeight}@{applied.DisplayFrequency} " +
                $"instead of {width}x{height}@{fps} to {gdiDeviceName}.");
        }
    }

    internal static AdvertisedDisplayMode ReadCurrentSourceMode(
        string gdiDeviceName) =>
        ToAdvertisedMode(ReadSourceDeviceMode(
            gdiDeviceName,
            EnumCurrentSettings,
            "current"));

    internal static AdvertisedDisplayMode ReadPersistedSourceMode(
        string gdiDeviceName) =>
        ToAdvertisedMode(ReadSourceDeviceMode(
            gdiDeviceName,
            EnumRegistrySettings,
            "persisted"));

    private static DeviceMode ReadSourceDeviceMode(
        string gdiDeviceName,
        int settingsMode,
        string description)
    {
        var mode = CreateDeviceMode();
        if (!EnumDisplaySettings(gdiDeviceName, settingsMode, ref mode))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not read the {description} display mode for {gdiDeviceName}.");
        }
        return mode;
    }

    private static AdvertisedDisplayMode ToAdvertisedMode(DeviceMode mode) =>
        new(
            checked((int)mode.PelsWidth),
            checked((int)mode.PelsHeight),
            checked((int)mode.DisplayFrequency));

    internal static IReadOnlyList<AdvertisedDisplayMode> EnumerateSourceModes(string gdiDeviceName)
    {
        EnsureWindows();
        var modes = new HashSet<AdvertisedDisplayMode>();
        for (var index = 0; ; index++)
        {
            var mode = CreateDeviceMode();
            if (!EnumDisplaySettings(gdiDeviceName, index, ref mode))
            {
                break;
            }
            modes.Add(new AdvertisedDisplayMode(
                checked((int)mode.PelsWidth),
                checked((int)mode.PelsHeight),
                checked((int)mode.DisplayFrequency)));
        }
        return modes
            .OrderBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .ThenBy(mode => mode.Fps)
            .ToArray();
    }

    private static DeviceMode CreateDeviceMode()
    {
        var mode = new DeviceMode { DeviceName = string.Empty, FormName = string.Empty };
        mode.Size = checked((ushort)Marshal.SizeOf<DeviceMode>());
        return mode;
    }

    private static string FormatClosestModes(
        IReadOnlyList<AdvertisedDisplayMode> modes,
        int width,
        int height,
        int fps)
    {
        if (modes.Count == 0) return "none";
        return string.Join(
            ", ",
            modes
                .OrderBy(mode =>
                    Math.Abs(mode.Width - width) +
                    Math.Abs(mode.Height - height) +
                    Math.Abs(mode.Fps - fps))
                .Take(8));
    }

    private static string DescribeDisplayChangeResult(int result) =>
        result switch
        {
            1 => "restart required",
            -1 => "general failure, DISP_CHANGE_FAILED",
            -2 => "unsupported mode, DISP_CHANGE_BADMODE",
            -3 => "registry update failed, DISP_CHANGE_NOTUPDATED",
            -4 => "invalid flags, DISP_CHANGE_BADFLAGS",
            -5 => "invalid parameters, DISP_CHANGE_BADPARAM",
            -6 => "dual-view mode rejected, DISP_CHANGE_BADDUALVIEW",
            _ => $"result {result}",
        };

    internal static bool TrySetAdvancedColorState(DisplayPathInfo path, bool enabled)
    {
        var state = new DisplaySetAdvancedColorState
        {
            Header = new DisplayDeviceInfoHeader
            {
                Type = SetAdvancedColorState,
                Size = checked((uint)Marshal.SizeOf<DisplaySetAdvancedColorState>()),
                AdapterId = path.TargetInfo.AdapterId,
                Id = path.TargetInfo.Id,
            },
            EnableAdvancedColor = enabled ? 1u : 0u,
        };
        return DisplayConfigSetDeviceInfo(ref state) == 0;
    }

    internal static void ValidateSinglePath(DisplayPathInfo path)
    {
        var paths = new[] { PreparePath(path) };
        var result = SetDisplayConfig(
            1,
            paths,
            0,
            null,
            SetValidate | SetUseSuppliedDisplayConfig | SetAllowChanges);
        ThrowIfFailed(result, "SetDisplayConfig validation");
    }

    internal static void ApplySinglePath(DisplayPathInfo path)
    {
        ApplyPaths(new[] { path });
    }

    internal static void ApplyPaths(
        DisplayPathInfo[] suppliedPaths,
        bool forceModeEnumeration = false)
    {
        EnsureWindows();
        if (suppliedPaths.Length == 0)
        {
            throw new ArgumentException("At least one display path is required.", nameof(suppliedPaths));
        }
        var paths = suppliedPaths.Select(PreparePath).ToArray();
        var validationResult = SetDisplayConfig(
            checked((uint)paths.Length),
            paths,
            0,
            null,
            SetValidate | SetUseSuppliedDisplayConfig | SetAllowChanges);
        ThrowIfFailed(validationResult, "SetDisplayConfig validation");
        var result = SetDisplayConfig(
            checked((uint)paths.Length),
            paths,
            0,
            null,
            SetApply |
            SetUseSuppliedDisplayConfig |
            SetAllowChanges |
            (forceModeEnumeration ? SetForceModeEnumeration : 0));
        ThrowIfFailed(result, "SetDisplayConfig apply");
    }

    internal static void Restore(DisplayConfiguration configuration)
    {
        EnsureWindows();
        var result = SetDisplayConfig(
            checked((uint)configuration.Paths.Length),
            configuration.Paths,
            checked((uint)configuration.Modes.Length),
            configuration.Modes,
            SetApply | SetUseSuppliedDisplayConfig | SetAllowChanges);
        ThrowIfFailed(result, "SetDisplayConfig restore");
    }

    private static DisplayPathInfo PreparePath(DisplayPathInfo path)
    {
        path.Flags |= PathActive;
        path.SourceInfo.ModeInfoIndex = PathModeIndexInvalid;
        path.TargetInfo.ModeInfoIndex = PathModeIndexInvalid;
        path.TargetInfo.RefreshRate = default;
        path.TargetInfo.ScanLineOrdering = 0;
        return path;
    }

    internal static byte[] StructuresToBytes<T>(T[] values) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[checked(size * values.Length)];
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            for (var index = 0; index < values.Length; index++)
            {
                Marshal.StructureToPtr(values[index], pointer, false);
                Marshal.Copy(pointer, bytes, index * size, size);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return bytes;
    }

    internal static T[] BytesToStructures<T>(byte[] bytes, int count) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        if (count < 0 || bytes.Length != checked(size * count))
        {
            throw new InvalidDataException($"Invalid serialized {typeof(T).Name} array.");
        }

        var values = new T[count];
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            for (var index = 0; index < count; index++)
            {
                Marshal.Copy(bytes, index * size, pointer, size);
                values[index] = Marshal.PtrToStructure<T>(pointer);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
        return values;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Display topology management is only supported on Windows.");
        }
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result != 0)
        {
            throw new Win32Exception(result, $"{operation} failed with Windows error {result}.");
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] DisplayPathInfo[] pathInfoArray,
        ref uint numModeInfoArrayElements,
        [Out] DisplayModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DisplayPathInfo[] pathArray,
        uint numModeInfoArrayElements,
        [In] DisplayModeInfo[]? modeInfoArray,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayTargetName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplaySourceName requestPacket);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigSetDeviceInfo(ref DisplaySetAdvancedColorState setPacket);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DeviceMode deviceMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DeviceMode deviceMode, IntPtr window, uint flags, IntPtr parameter);
}

internal sealed record DisplayConfiguration(DisplayPathInfo[] Paths, DisplayModeInfo[] Modes);
internal readonly record struct AdvertisedDisplayMode(int Width, int Height, int Fps)
{
    public override string ToString() => $"{Width}x{Height}@{Fps}";
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayLuid
{
    internal uint LowPart;
    internal int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayRational
{
    internal uint Numerator;
    internal uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathSourceInfo
{
    internal DisplayLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathTargetInfo
{
    internal DisplayLuid AdapterId;
    internal uint Id;
    internal uint ModeInfoIndex;
    internal int OutputTechnology;
    internal int Rotation;
    internal int Scaling;
    internal DisplayRational RefreshRate;
    internal int ScanLineOrdering;
    internal int TargetAvailable;
    internal uint StatusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPathInfo
{
    internal DisplayPathSourceInfo SourceInfo;
    internal DisplayPathTargetInfo TargetInfo;
    internal uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayPoint
{
    internal int X;
    internal int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayRegion
{
    internal uint Width;
    internal uint Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayRect
{
    internal int Left;
    internal int Top;
    internal int Right;
    internal int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplaySourceMode
{
    internal uint Width;
    internal uint Height;
    internal int PixelFormat;
    internal DisplayPoint Position;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayVideoSignalInfo
{
    internal ulong PixelRate;
    internal DisplayRational HorizontalSyncFrequency;
    internal DisplayRational VerticalSyncFrequency;
    internal DisplayRegion ActiveSize;
    internal DisplayRegion TotalSize;
    internal uint VideoStandard;
    internal int ScanLineOrdering;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayTargetMode
{
    internal DisplayVideoSignalInfo TargetVideoSignalInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayDesktopImageInfo
{
    internal DisplayPoint PathSourceSize;
    internal DisplayRect DesktopImageRegion;
    internal DisplayRect DesktopImageClip;
}

[StructLayout(LayoutKind.Explicit, Size = 48)]
internal struct DisplayModeUnion
{
    [FieldOffset(0)] internal DisplayTargetMode TargetMode;
    [FieldOffset(0)] internal DisplaySourceMode SourceMode;
    [FieldOffset(0)] internal DisplayDesktopImageInfo DesktopImageInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayModeInfo
{
    internal int InfoType;
    internal uint Id;
    internal DisplayLuid AdapterId;
    internal DisplayModeUnion Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplayDeviceInfoHeader
{
    internal int Type;
    internal uint Size;
    internal DisplayLuid AdapterId;
    internal uint Id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DisplaySetAdvancedColorState
{
    internal DisplayDeviceInfoHeader Header;
    internal uint EnableAdvancedColor;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplayTargetName
{
    internal DisplayDeviceInfoHeader Header;
    internal uint Flags;
    internal int OutputTechnology;
    internal ushort EdidManufactureId;
    internal ushort EdidProductCodeId;
    internal uint ConnectorInstance;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    internal string MonitorFriendlyDeviceName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    internal string MonitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DisplaySourceName
{
    internal DisplayDeviceInfoHeader Header;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    internal string ViewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DeviceMode
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string DeviceName;
    internal ushort SpecVersion;
    internal ushort DriverVersion;
    internal ushort Size;
    internal ushort DriverExtra;
    internal uint Fields;
    internal int PositionX;
    internal int PositionY;
    internal uint DisplayOrientation;
    internal uint DisplayFixedOutput;
    internal short Color;
    internal short Duplex;
    internal short YResolution;
    internal short TTOption;
    internal short Collate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FormName;
    internal ushort LogPixels;
    internal uint BitsPerPel;
    internal uint PelsWidth;
    internal uint PelsHeight;
    internal uint DisplayFlags;
    internal uint DisplayFrequency;
    internal uint ICMMethod;
    internal uint ICMIntent;
    internal uint MediaType;
    internal uint DitherType;
    internal uint Reserved1;
    internal uint Reserved2;
    internal uint PanningWidth;
    internal uint PanningHeight;
}
