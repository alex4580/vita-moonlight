namespace VitaMoonlight.Host;

internal readonly record struct VitaDisplayMode(int Width, int Height, int Fps)
{
    public override string ToString() => $"{Width}x{Height}@{Fps}";
}

internal static class VitaDisplayModes
{
    internal static VitaDisplayMode Native { get; } = new(960, 544, 60);

    internal static IReadOnlyList<VitaDisplayMode> Supported { get; } =
    [
        Native,
        new VitaDisplayMode(960, 540, 60),
        new VitaDisplayMode(1280, 720, 60),
    ];

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
