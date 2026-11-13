using System.Globalization;
using System.Windows.Media;

namespace QuickClip.Themes;

internal enum ThemeChoice { Dark, Amoled, Slate, Light, Custom }

internal enum ThemeColor
{
    Background, Surface, ElevatedSurface, Sidebar, Border, PrimaryText, SecondaryText, Accent, AccentHover,
    Selection, Success, Warning, Danger, Disabled, OverlayBackground,
}

internal static class ThemePalette
{
    internal static readonly ThemeColor[] Colors = Enum.GetValues<ThemeColor>();

    private static readonly IReadOnlyDictionary<ThemeColor, Color> Dark = Make(
        "#202020 #1C1C1C #232323 #171717 #525252 #FFFFFF #A8A8A8 #80CCFF #A7DDFF #31373B #5CEDAC #FFB900 #FF626D #777777 #181818");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Amoled = Make(
        "#000000 #101010 #1A1A1A #000000 #656565 #FFFFFF #B5B5B5 #74D6D0 #9CE9E4 #163534 #65E2AE #FFD072 #FF7982 #828282 #080808");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Slate = Make(
        "#202936 #283342 #344152 #18212D #687D97 #F2F6FB #BDCBDA #9DCFFF #B8DCFF #3A5068 #71E1B1 #FFD084 #FF8890 #8494AA #1B2431");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Light = Make(
        "#F4F6F8 #FFFFFF #E8EDF2 #E3E9EF #63758A #172332 #41546B #155589 #174974 #C7DEF1 #087247 #815000 #AD2534 #647181 #F8FAFC");

    internal static IReadOnlyDictionary<ThemeColor, Color> Get(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Amoled => Amoled,
        ThemeChoice.Slate => Slate,
        ThemeChoice.Light => Light,
        _ => Dark,
    };

    internal static bool TryParseColor(string? hex, out Color color)
    {
        color = default;
        if (hex is not { Length: 7 } || hex[0] != '#' ||
            !uint.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    internal static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    internal static IEnumerable<string> ReadabilityWarnings(IReadOnlyDictionary<ThemeColor, Color> palette)
    {
        foreach (var surface in new[] { ThemeColor.Background, ThemeColor.Surface, ThemeColor.ElevatedSurface,
                     ThemeColor.Sidebar, ThemeColor.Selection, ThemeColor.OverlayBackground })
        {
            foreach (var text in new[] { ThemeColor.PrimaryText, ThemeColor.SecondaryText })
                if (Contrast(palette[text], palette[surface]) < 4.5)
                    yield return $"{text} on {surface} is below 4.5:1.";
        }
        foreach (var signal in new[] { ThemeColor.Accent, ThemeColor.Success, ThemeColor.Warning, ThemeColor.Danger })
            if (Contrast(palette[signal], palette[ThemeColor.Surface]) < 3)
                yield return $"{signal} on Surface is below 3:1.";
    }

    internal static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static double Luminance(Color color)
    {
        static double Linear(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
    }

    private static IReadOnlyDictionary<ThemeColor, Color> Make(string values) =>
        Colors.Zip(values.Split(' '), (key, hex) => (key, color: (Color)ColorConverter.ConvertFromString(hex)))
            .ToDictionary(entry => entry.key, entry => entry.color);
}
