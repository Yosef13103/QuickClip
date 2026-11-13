using System.Text;
using System.Windows;
using System.Windows.Media;

namespace QuickClip.Themes;

internal sealed class ThemeResources
{
    private readonly ResourceDictionary _resources = new();
    private readonly Dictionary<ThemeChoice, Dictionary<object, Brush>> _builtIns = [];

    internal void Attach(Application application) => application.Resources.MergedDictionaries.Add(_resources);

    internal void Apply(Application application, AppSettings settings)
    {
        var choice = AppProfile.IsPersonal ? ThemeChoiceFrom(settings.Theme) : ThemeChoice.Dark;
        if (!AppProfile.IsPersonal)
        {
            Apply(_resources, ThemeChoice.Dark);
            return;
        }
        ThemeDocument? custom = choice == ThemeChoice.Custom ? ReadSaved(settings.CustomThemeJson) : null;
        if (choice == ThemeChoice.Custom && (custom == null || ThemePalette.ReadabilityWarnings(custom.Palette).Any()))
            choice = ThemeChoice.Dark;
        application.ThemeMode = SystemParameters.HighContrast ? ThemeMode.System :
            choice == ThemeChoice.Light || choice == ThemeChoice.Custom &&
            ThemePalette.Contrast(custom!.Palette[ThemeColor.Background], Colors.Black) > 5
                ? ThemeMode.Light : ThemeMode.Dark;
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
        if (choice != ThemeChoice.Custom && !_builtIns.TryGetValue(choice, out _))
            _builtIns.Add(choice, BrushesFor(ThemePalette.Get(choice), overrideFluent: choice != ThemeChoice.Dark));
        var brushes = choice == ThemeChoice.Custom
            ? BrushesFor(custom ?? ThemePalette.Get(ThemeChoice.Dark), overrideFluent)
            : _builtIns[choice];
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
        brushes["DangerHoverBrush"] = Frozen(danger);
        brushes["DangerPressedBrush"] = Frozen(danger);
        brushes["OnDangerBrush"] = OnColor(danger);
        brushes["SwitchThumbBrush"] = OnColor(colors[ThemeColor.Accent]);
        // Semantic foregrounds are available to all palettes, including the legacy Dark and high-contrast paths.
        brushes["OnAccentBrush"] = OnColor(colors[ThemeColor.Accent]);
        brushes["OnAccentHoverBrush"] = OnColor(colors[ThemeColor.AccentHover]);
        if (ReferenceEquals(colors, ThemePalette.Get(ThemeChoice.Dark)) && !overrideFluent)
        {
            brushes["DangerBrush"] = Frozen(Color.FromRgb(0xAE, 0x2D, 0x37));
            brushes["DangerHoverBrush"] = Frozen(Color.FromRgb(0xC3, 0x37, 0x44));
            brushes["DangerPressedBrush"] = Frozen(Color.FromRgb(0x8C, 0x22, 0x2C));
            brushes["OnDangerBrush"] = Brushes.White;
            brushes["SwitchThumbBrush"] = Brushes.White;
        }
        if (!overrideFluent) return brushes;

        Map(ThemeColor.Background, "ApplicationBackgroundBrush", "WindowBackground");
        Map(ThemeColor.PrimaryText, "TextFillColorPrimaryBrush", "ToolTipForeground", "ToolTipForegroundBrush",
            "ComboBoxForeground", "ComboBoxItemForeground", "MenuFlyoutItemForeground", "ButtonForeground",
            "TextBoxForeground", SystemColors.WindowTextBrushKey);
        Map(ThemeColor.SecondaryText, "TextFillColorSecondaryBrush", "TextFillColorTertiaryBrush", "ScrollBarThumbFill");
        Map(ThemeColor.Disabled, "TextFillColorDisabledBrush", "AccentFillColorDisabledBrush");
        Map(ThemeColor.Surface, "ControlFillColorDefaultBrush", "ComboBoxBackground", "TextBoxBackground");
        Map(ThemeColor.ElevatedSurface, "ControlFillColorSecondaryBrush", "ControlFillColorInputActiveBrush",
            "ComboBoxDropDownBackground", "TextBoxBackgroundFocused", "ScrollBarTrackFill",
            "ComboBoxItemBackground", "MenuFlyoutPresenterBackground", "MenuFlyoutItemBackground");
        Map(ThemeColor.Selection, "ControlFillColorTertiaryBrush", "ComboBoxItemBackgroundSelected",
            "ComboBoxItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPointerOver",
            "MenuFlyoutItemBackgroundSelected", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed");
        Map(ThemeColor.Border, "ControlStrokeColorDefaultBrush", "ControlStrokeColorSecondaryBrush", "ToolTipBorderBrush");
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
