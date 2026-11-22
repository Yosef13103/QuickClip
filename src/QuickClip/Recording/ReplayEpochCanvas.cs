namespace QuickClip.Recording;

// The native source dimensions remain immutable. Output canvas selection affects
// offline normalization only, never capture permission, replay retention or encoder settings.
internal enum ReplayEpochOutputSize { Auto, Balanced, P1080, P1440, P2160, Custom }

internal sealed record ReplayEpochOutputOptions(
    ReplayEpochOutputSize Size = ReplayEpochOutputSize.Auto,
    int CustomWidth = 0, int CustomHeight = 0);

internal static class ReplayEpochCanvas
{
    internal static (int Width, int Height) Resolve(
        ReplayEpochExportPlan plan, ReplayEpochOutputOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new ReplayEpochOutputOptions();
        if (plan.Width < 16 || plan.Height < 16 ||
            (plan.Width & 1) != 0 || (plan.Height & 1) != 0)
            throw new InvalidOperationException("The source epoch canvas is invalid.");

        int height = options.Size switch
        {
            ReplayEpochOutputSize.Auto => plan.Height,
            ReplayEpochOutputSize.Balanced => Math.Min(plan.Height, 1440),
            ReplayEpochOutputSize.P1080 => 1080,
            ReplayEpochOutputSize.P1440 => 1440,
            ReplayEpochOutputSize.P2160 => 2160,
            ReplayEpochOutputSize.Custom => options.CustomHeight,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };

        // Presets describe vertical resolution, not forced 16:9 geometry:
        // an ultrawide 3440x1440 canvas stays ultrawide at every preset.
        int width = options.Size is ReplayEpochOutputSize.Auto
            ? plan.Width
            : options.Size is ReplayEpochOutputSize.Custom
                ? options.CustomWidth
                : height > 0
                    ? checked((int)Math.Round((double)plan.Width * height / plan.Height / 2,
                        MidpointRounding.AwayFromZero)) * 2
                    : 0;

        if (width < 16 || height < 16 || width > 8192 || height > 4320 ||
            (width & 1) != 0 || (height & 1) != 0 ||
            (long)width * height > 34_000_000)
            throw new InvalidOperationException("The requested epoch canvas exceeds supported dimensions or pixel budget.");

        return (width, height);
    }
}
