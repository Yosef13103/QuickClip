using System.Text;
using System.Windows;
using System.Windows.Media;

namespace QuickClip.Themes;

internal sealed class ThemeResources
{
    private readonly ResourceDictionary _resources = new();
    private readonly Dictionary<(ThemeChoice Choice, bool OverrideFluent), Dictionary<object, Brush>> _builtIns = [];

    internal void Attach(Application application) => application.Resources.MergedDictionaries.Add(_resources);

    internal void Apply(Application application, AppSettings settings)
    {
        var choice = AppProfile.IsPersonal ? ThemeChoiceFrom(settings.Theme) : ThemeChoice.Dark;
        if (!AppProfile.IsPersonal)
        {
            Apply(_resources, ThemeChoice.Dark, overrideFluent: false);
            return;
        }
        ThemeDocument? custom = choice == ThemeChoice.Custom ? ReadSaved(settings.CustomThemeJson) : null;
        if (choice == ThemeChoice.Custom && (custom == null || ThemePalette.ReadabilityWarnings(custom.Palette).Any()))
            choice = ThemeChoice.Dark;
        var mode = SystemParameters.HighContrast ? ThemeMode.System :
            choice == ThemeChoice.Light || choice == ThemeChoice.Custom &&
            ThemePalette.Contrast(custom!.Palette[ThemeColor.Background], Colors.Black) > 5
                ? ThemeMode.Light : ThemeMode.Dark;
        if (!Equals(application.ThemeMode, mode)) application.ThemeMode = mode;
        if (SystemParameters.HighContrast)
        {
            Apply(_resources, ThemeChoice.Custom, HighContrastPalette(), overrideFluent: false);
            return;
        }
        Apply(_resources, choice, custom?.Palette);
    }

    internal void Apply(ResourceDictionary resources, ThemeChoice choice, IReadOnlyDictionary<ThemeColor, Color>? custom = null,
        bool overrideFluent = true)
    {
        var cacheKey = (choice, overrideFluent);
        if (choice != ThemeChoice.Custom && !_builtIns.ContainsKey(cacheKey))
            _builtIns.Add(cacheKey, BrushesFor(ThemePalette.Get(choice), overrideFluent));
        var brushes = choice == ThemeChoice.Custom
            ? BrushesFor(custom ?? ThemePalette.Get(ThemeChoice.Dark), overrideFluent)
            : _builtIns[cacheKey];
        foreach (object key in resources.Keys.Cast<object>().Except(brushes.Keys).ToArray()) resources.Remove(key);
        foreach (var (key, brush) in brushes)
            if (!ReferenceEquals(resources[key], brush)) resources[key] = brush;
    }

    internal static ThemeChoice ThemeChoiceFrom(string? name) =>
        Enum.TryParse(name, out ThemeChoice choice) && Enum.IsDefined(choice) ? choice : ThemeChoice.Dark;

    internal static ThemeDocument? ReadSaved(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > ThemeDocument.MaxBytes) return null;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            return ThemeDocument.Read(stream);
        }
        catch (InvalidDataException) { return null; }
    }

    internal static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Dictionary<object, Brush> BrushesFor(IReadOnlyDictionary<ThemeColor, Color> colors, bool overrideFluent)
    {
        var brushes = new Dictionary<object, Brush>();
        foreach (var (key, color) in colors) brushes[key + "Brush"] = Frozen(color);
        void Map(ThemeColor token, params object[] keys)
        {
            foreach (object key in keys) brushes[key] = brushes[token + "Brush"];
        }
        Map(ThemeColor.Surface, "PanelBrush");
        Map(ThemeColor.ElevatedSurface, "CardBrush");
        Map(ThemeColor.Sidebar, "RailBrush");
        Map(ThemeColor.SecondaryText, "SubtleText");
        Map(ThemeColor.Danger, "RecordingRed");
        brushes["ModalScrimBrush"] = Frozen(Color.FromArgb(140, 0, 0, 0));
        var danger = colors[ThemeColor.Danger];
        // Move away from the chosen foreground to keep destructive states readable.
        var dangerInk = ThemePalette.Contrast(danger, Colors.Black) >= ThemePalette.Contrast(danger, Colors.White)
            ? Colors.Black : Colors.White;
        var awayFromInk = dangerInk == Colors.Black ? Colors.White : Colors.Black;
        brushes["DangerHoverBrush"] = Frozen(Blend(danger, awayFromInk, .12));
        brushes["DangerPressedBrush"] = Frozen(Blend(danger, awayFromInk, .22));
        brushes["OnDangerBrush"] = OnColor(danger);
        brushes["SwitchThumbBrush"] = OnColor(colors[ThemeColor.Accent]);
        brushes["SwitchHoverThumbBrush"] = OnColor(colors[ThemeColor.AccentHover]);
        Map(ThemeColor.SecondaryText, "SwitchOffThumbBrush", "SwitchOutlineBrush");
        Map(ThemeColor.ElevatedSurface, "SwitchOffBrush");
        Map(ThemeColor.Surface, "InternalSidebarBrush");
        // Main rail selection should be distinct even when rail and page are light.
        Map(ThemeColor.Accent, "RailSelectedBrush");
        brushes["RailSelectedTextBrush"] = OnColor(colors[ThemeColor.Accent]);
        brushes["DividerBrush"] = Frozen(Blend(colors[ThemeColor.Surface], colors[ThemeColor.Border], .38));
        // Semantic foregrounds are available to all palettes, including the legacy Dark and high-contrast paths.
        brushes["OnAccentBrush"] = OnColor(colors[ThemeColor.Accent]);
        brushes["OnWarningBrush"] = OnColor(colors[ThemeColor.Warning]);
        brushes["OnAccentHoverBrush"] = OnColor(colors[ThemeColor.AccentHover]);
        if (ReferenceEquals(colors, ThemePalette.Get(ThemeChoice.Dark)) && !overrideFluent)
        {
            brushes["DangerBrush"] = Frozen(Color.FromRgb(0xAE, 0x2D, 0x37));
            brushes["DangerHoverBrush"] = Frozen(Color.FromRgb(0xC3, 0x37, 0x44));
            brushes["DangerPressedBrush"] = Frozen(Color.FromRgb(0x8C, 0x22, 0x2C));
            brushes["OnDangerBrush"] = Brushes.White;
        }
        if (!overrideFluent) return brushes;

        Map(ThemeColor.Background, "ApplicationBackgroundBrush", "WindowBackground");
        Map(ThemeColor.PrimaryText, "TextFillColorPrimaryBrush", "ToolTipForeground", "ToolTipForegroundBrush",
            "ComboBoxForeground", "ComboBoxItemForeground", "MenuFlyoutItemForeground", "ButtonForeground",
            "TextBoxForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed",
            "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused",
            "ComboBoxForegroundFocusedPressed", SystemColors.WindowTextBrushKey);
        Map(ThemeColor.SecondaryText, "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "ScrollBarThumbFill");
        Map(ThemeColor.Disabled, "TextFillColorDisabledBrush", "AccentFillColorDisabledBrush",
            "ButtonForegroundDisabled", "ComboBoxForegroundDisabled");
        Map(ThemeColor.Surface, "ControlFillColorDefaultBrush", "ComboBoxBackground", "TextBoxBackground", "ButtonBackground");
        Map(ThemeColor.ElevatedSurface, "ControlFillColorSecondaryBrush", "ControlFillColorInputActiveBrush",
            "ComboBoxDropDownBackground", "TextBoxBackgroundFocused", "ScrollBarTrackFill",
            "ComboBoxItemBackground", "MenuFlyoutPresenterBackground", "MenuFlyoutItemBackground",
            "ButtonBackgroundPointerOver", "ComboBoxBackgroundPointerOver");
        Map(ThemeColor.Selection, "ControlFillColorTertiaryBrush", "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver",
            "MenuFlyoutItemBackgroundSelected", "ButtonBackgroundPressed", "ComboBoxBackgroundPressed");
        Map(ThemeColor.Border, "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "ToolTipBorderBrush",
            "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
            "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed");
        Map(ThemeColor.OverlayBackground, "ToolTipBackground", "ToolTipBackgroundBrush");
        Map(ThemeColor.Accent, "AccentFillColorDefaultBrush", "AccentFillColorSelectedTextBackgroundBrush",
            "AccentButtonBackground", "SliderThumbFill", "SliderThumbFillPointerOver",
            "SliderTrackValueFill", "SliderTrackValueFillPointerOver", SystemColors.HighlightBrushKey);
        Map(ThemeColor.AccentHover, "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentButtonBackgroundPointerOver", "AccentButtonBackgroundPressed");
        brushes["AccentButtonForeground"] = brushes["TextOnAccentFillColorPrimaryBrush"] =
            brushes[SystemColors.HighlightTextBrushKey] = OnColor(colors[ThemeColor.Accent]);
        brushes["AccentButtonForegroundPointerOver"] = brushes["AccentButtonForegroundPressed"] =
            OnColor(colors[ThemeColor.AccentHover]);
        return brushes;
    }

    private static Color Blend(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    private static Brush OnColor(Color background) => ThemePalette.Contrast(background, Colors.Black) >=
        ThemePalette.Contrast(background, Colors.White) ? Brushes.Black : Brushes.White;

    private static Dictionary<ThemeColor, Color> HighContrastPalette() => ThemePalette.Colors.ToDictionary(key => key, key => key switch
    {
        ThemeColor.PrimaryText or ThemeColor.SecondaryText or ThemeColor.Success or ThemeColor.Warning or ThemeColor.Danger or
            ThemeColor.Border or ThemeColor.Accent or ThemeColor.AccentHover => SystemColors.WindowTextColor,
        ThemeColor.Disabled => SystemColors.GrayTextColor,
        _ => SystemColors.WindowColor,
    });
}
