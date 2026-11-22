namespace QuickClip.Mpv;

/// <summary>mpv's video-zoom is log2 magnification, not export crop or timeline zoom.</summary>
internal static class VideoPreviewZoom
{
    internal const double Min = -1; // 50%
    internal const double Max = 2;  // 400%

    internal static double Step(double value, int wheelDelta) =>
        Math.Clamp(Math.Round(value + wheelDelta / 120d * .25, 4), Min, Max);

    internal static int Percent(double value) => (int)Math.Round(100 * Math.Pow(2, value));
}
