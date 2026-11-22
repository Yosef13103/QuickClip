namespace QuickClip.Mpv;

/// <summary>Pure preview-only viewport geometry. mpv zoom is log2; pan is a fraction of the scaled image.</summary>
internal static class VideoPreviewZoom
{
    internal const double Min = -1; // 50%
    internal const double Max = 2;  // 400%

    internal static double Step(double value, int wheelDelta) =>
        Math.Clamp(Math.Round(value + wheelDelta / 120d * .25, 4), Min, Max);

    internal static int Percent(double value) => (int)Math.Round(100 * Math.Pow(2, value));

    internal readonly record struct Transform(double Zoom, double PanX, double PanY);

    internal static Transform ZoomAt(Transform current, int delta, double x, double y,
        double viewportWidth, double viewportHeight, double videoAspect)
    {
        double next = Step(current.Zoom, delta);
        if (next == current.Zoom) return current;
        var oldSize = ScaledSize(viewportWidth, viewportHeight, videoAspect, current.Zoom);
        var newSize = ScaledSize(viewportWidth, viewportHeight, videoAspect, next);
        if (oldSize.Width <= 0 || oldSize.Height <= 0) return new(next, 0, 0);
        // Keep the same image point under the pointer while scaling.
        double px = current.PanX + (x - viewportWidth / 2) * (1 / newSize.Width - 1 / oldSize.Width);
        double py = current.PanY + (y - viewportHeight / 2) * (1 / newSize.Height - 1 / oldSize.Height);
        return Clamp(new(next, px, py), viewportWidth, viewportHeight, videoAspect);
    }

    internal static Transform PanBy(Transform current, double dx, double dy,
        double viewportWidth, double viewportHeight, double videoAspect)
    {
        var image = ScaledSize(viewportWidth, viewportHeight, videoAspect, current.Zoom);
        if (image.Width <= 0 || image.Height <= 0) return current;
        return Clamp(current with
        {
            PanX = current.PanX + dx / image.Width,
            PanY = current.PanY + dy / image.Height,
        }, viewportWidth, viewportHeight, videoAspect);
    }

    internal static Transform Clamp(Transform current, double width, double height, double aspect)
    {
        var image = ScaledSize(width, height, aspect, current.Zoom);
        if (image.Width <= 0 || image.Height <= 0) return current with { PanX = 0, PanY = 0 };
        // Unlike unbounded mpv pan, never create empty space beyond the image edges.
        double maxX = Math.Max(0, (image.Width - width) / (2 * image.Width));
        double maxY = Math.Max(0, (image.Height - height) / (2 * image.Height));
        return current with
        {
            PanX = Math.Clamp(current.PanX, -maxX, maxX),
            PanY = Math.Clamp(current.PanY, -maxY, maxY),
        };
    }

    internal static (double Width, double Height) ScaledSize(
        double viewportWidth, double viewportHeight, double videoAspect, double zoom)
    {
        if (!(viewportWidth > 0 && viewportHeight > 0 && videoAspect > 0) ||
            !double.IsFinite(videoAspect)) return (0, 0);
        double fittedWidth = Math.Min(viewportWidth, viewportHeight * videoAspect);
        double fittedHeight = fittedWidth / videoAspect;
        double scale = Math.Pow(2, zoom);
        return (fittedWidth * scale, fittedHeight * scale);
    }
}
