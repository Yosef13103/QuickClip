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
        "#181A1F #202329 #282C34 #14161A #545C69 #F3F5F8 #B0B8C5 #83B9FF #A8CDFF #303D50 #62D9A0 #F6C76E #FF828B #777F8D #22262D");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Amoled = Make(
        "#000000 #101214 #1C2023 #000000 #50585D #F4F7F8 #AFBCC2 #74D6D0 #9CE9E4 #183739 #65E2AE #FFD072 #FF7982 #758087 #101416");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Slate = Make(
        "#1D2532 #253041 #303D50 #161E2A #65768D #F2F5FA #BAC8D9 #91C5FF #B6D9FF #34475D #72DDB0 #F4CB80 #FF8993 #8495AB #253041");
    private static readonly IReadOnlyDictionary<ThemeColor, Color> Light = Make(
        "#F4F6FA #FFFFFF #E8EDF4 #E4E9F0 #77869A #182332 #46566A #225DA1 #194C8A #DCE8F8 #087247 #815000 #AD2534 #687688 #FFFFFF");

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
