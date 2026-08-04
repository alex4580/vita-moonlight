namespace VitaMoonlight.Host;

internal readonly record struct VitaDisplayMode(int Width, int Height, int Fps)
{
    public override string ToString() => $"{Width}x{Height}@{Fps}";
}

internal readonly record struct VitaStreamMode(
    VitaDisplayMode DesktopMode,
    int StreamFps)
{
    public override string ToString() =>
        $"{DesktopMode.Width}x{DesktopMode.Height} at {StreamFps} FPS " +
        $"(Windows {DesktopMode.Fps} Hz)";
}

internal static class VitaDisplayModes
{
    internal const int DesktopRefreshRate = 60;

    internal static VitaDisplayMode Native { get; } =
        new(960, 544, DesktopRefreshRate);

    internal static IReadOnlyList<VitaDisplayMode> Supported { get; } =
    [
        Native,
        new VitaDisplayMode(960, 540, DesktopRefreshRate),
        new VitaDisplayMode(1280, 720, DesktopRefreshRate),
    ];

    internal static IReadOnlyList<int> SupportedStreamFrameRates { get; } =
    [
        24,
        30,
        40,
        50,
        60,
    ];

    internal static VitaStreamMode RequireSupportedStreamMode(
        int width,
        int height,
        int streamFps)
    {
        if (TryGetSupportedStreamMode(
                width,
                height,
                streamFps,
                out var streamMode))
        {
            return streamMode;
        }

        throw new ArgumentOutOfRangeException(
            nameof(width),
            "Stream mode must use one of the Vita resolutions " +
            $"({string.Join(", ", Supported.Select(mode => $"{mode.Width}x{mode.Height}"))}) " +
            "and one of the supported frame rates " +
            $"({string.Join(", ", SupportedStreamFrameRates)} FPS). " +
            $"Windows always runs the virtual display at {DesktopRefreshRate} Hz.");
    }

    internal static bool TryGetSupportedStreamMode(
        int width,
        int height,
        int streamFps,
        out VitaStreamMode streamMode)
    {
        var desktopMode = Supported.FirstOrDefault(mode =>
            mode.Width == width && mode.Height == height);
        if (desktopMode == default ||
            !SupportedStreamFrameRates.Contains(streamFps))
        {
            streamMode = default;
            return false;
        }

        streamMode = new VitaStreamMode(desktopMode, streamFps);
        return true;
    }

    internal static VitaDisplayMode RequireSupported(int width, int height, int fps)
    {
        var requested = new VitaDisplayMode(width, height, fps);
        if (Supported.Contains(requested))
        {
            return requested;
        }

        throw new ArgumentOutOfRangeException(
            nameof(width),
            $"Runtime virtual-display mode must be one of: {string.Join(", ", Supported)}.");
    }
}
