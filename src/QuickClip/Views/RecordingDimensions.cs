using System.Globalization;
using QuickClip.Recording;

namespace QuickClip.Views;

internal static class RecordingDimensions
{
    internal const int MinimumCustomDimension = 256;

    internal static bool TryParseAspect(string value, out string aspect)
    {
        aspect = "";
        var parts = value.Split(':');
        if (parts.Length != 2 || !TryPixels(parts[0], out int width) || !TryPixels(parts[1], out int height))
            return false;
        aspect = Aspect(width, height);
        return true;
    }

    internal static string Aspect(int width, int height)
    {
        int a = width, b = height;
        while (b != 0) (a, b) = (b, a % b);
        return FormattableString.Invariant($"{width / a}:{height / a}");
    }

    internal static bool TryPixels(string value, out int pixels) =>
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out pixels) && pixels > 0;

    internal static IEnumerable<int> AvailableResolutions(RecordingArea area) =>
        RecordingPresets.Resolutions.Where(limit => limit > 0 && FitsPreset(area, limit));

    private static bool FitsPreset(RecordingArea area, int limit)
    {
        if (limit > Math.Min(area.Width, area.Height)) return false;
        var (width, height) = RecordingPresets.OutputSize(area.Width, area.Height, limit);
        return width <= area.Width && height <= area.Height;
    }

    internal static (int Width, int Height) MatchDimension(RecordingArea area, int value, bool editWidth)
    {
        if (area.Width <= 0 || area.Height <= 0 || value <= 0 ||
            value > (editWidth ? area.Width : area.Height)) return default;
        bool widthIsShortSide = area.Width <= area.Height;
        bool editedShortSide = editWidth == widthIsShortSide;
        int step = widthIsShortSide ? 8 : 2;
        int shortSide = editedShortSide ? value : (int)Math.Round(value *
            (Math.Min(area.Width, area.Height) / (double)Math.Max(area.Width, area.Height)) / step) * step;
        return shortSide > 0 ? RecordingPresets.OutputSize(area.Width, area.Height, shortSide) : default;
    }

    internal static string? SizeError(int width, int height, int sourceWidth, int sourceHeight)
    {
        if (width < MinimumCustomDimension || height < MinimumCustomDimension)
            return $"Use at least {MinimumCustomDimension} pixels on each side for a custom output.";
        if (width > sourceWidth || height > sourceHeight)
            return "The output must fit inside the source without upscaling.";
        if (width % 8 != 0 || height % 2 != 0)
            return "Use a width divisible by 8 and an even height.";
        return null;
    }

    internal static bool CanProduce(RecordingArea area, int width, int height) =>
        width > 0 && height > 0 && width <= area.Width && height <= area.Height &&
        RecordingPresets.OutputSize(area.Width, area.Height, Math.Min(width, height)) == (width, height);
}
