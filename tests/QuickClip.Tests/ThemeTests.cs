using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using QuickClip.Themes;

namespace QuickClip.Tests;

public sealed class ThemeTests
{
    [Theory]
    [InlineData("Dark")]
    [InlineData("Amoled")]
    [InlineData("Slate")]
    [InlineData("Light")]
    public void Built_in_palettes_cover_all_tokens_and_pass_readability(string name)
    {
        var theme = ThemeResources.ThemeChoiceFrom(name);
        var palette = ThemePalette.Get(theme);
        Assert.Equal(ThemePalette.Colors.Length, palette.Count);
        Assert.Empty(ThemePalette.ReadabilityWarnings(palette));
        if (theme == ThemeChoice.Amoled) Assert.Equal(Colors.Black, palette[ThemeColor.Background]);
    }

    [Fact]
    public void Versioned_palette_and_selected_theme_round_trip_through_settings()
    {
        string json = ExportedPalette();
        var settings = new AppSettings { Theme = "Custom", CustomThemeJson = json };
        var restored = AppSettings.Parse(settings.ToJson());
        Assert.Equal(ThemeChoice.Custom, ThemeResources.ThemeChoiceFrom(restored.Theme));
        var theme = Assert.IsType<ThemeDocument>(ThemeResources.ReadSaved(restored.CustomThemeJson));
        Assert.Equal("Custom sample", theme.Name);
        Assert.Equal(ThemePalette.Get(ThemeChoice.Slate), theme.Palette);
    }

    [Fact]
    public void Invalid_and_oversized_imports_are_rejected()
    {
        string valid = ExportedPalette();
        foreach (string json in new[]
        {
            valid.Replace("\"version\": 1", "\"version\": 2"),
            valid.Replace("\"version\": 1", "\"version\": 1, \"version\": 1"),
            valid.Replace("\"Background\"", "\"Background\": \"#000000\", \"Background\""),
            valid.Replace("\"Background\"", "\"RemoteXaml\""),
            valid.Replace(ThemePalette.Hex(ThemePalette.Get(ThemeChoice.Slate)[ThemeColor.Background]), "https://example.invalid/theme"),
            valid.Replace("Custom sample", new string('x', ThemeDocument.MaxNameLength + 1)),
            valid.Replace("Custom sample", " "),
            valid + new string(' ', ThemeDocument.MaxBytes),
        })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            Assert.Throws<InvalidDataException>(() => ThemeDocument.Read(stream));
        }
        Assert.Null(ThemeResources.ReadSaved("{"));
    }

    [Fact]
    public void Low_contrast_is_detected_for_text_and_signals()
    {
        var palette = new Dictionary<ThemeColor, Color>(ThemePalette.Get(ThemeChoice.Dark));
        palette[ThemeColor.PrimaryText] = palette[ThemeColor.Surface];
        palette[ThemeColor.Accent] = palette[ThemeColor.Surface];
        Assert.Contains(ThemePalette.ReadabilityWarnings(palette), warning => warning.StartsWith("PrimaryText"));
        Assert.Contains(ThemePalette.ReadabilityWarnings(palette), warning => warning.StartsWith("Accent"));
    }

    [Fact]
    public async Task Repeated_theme_changes_reuse_built_in_brushes_and_release_replaced_custom_brushes()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var (resources, discarded) = SwitchThemes();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.All(discarded, reference => Assert.False(reference.IsAlive));
                GC.KeepAlive(resources);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResourceDictionary, WeakReference[]) SwitchThemes()
    {
        var resources = new ResourceDictionary();
        var service = new ThemeResources();
        service.Apply(resources, ThemeChoice.Dark);
        var baseline = resources["BackgroundBrush"];
        var discarded = new WeakReference[256];
        for (int i = 0; i < discarded.Length; i++)
        {
            service.Apply(resources, ThemeChoice.Custom, ThemePalette.Get(ThemeChoice.Slate));
            discarded[i] = new WeakReference(resources["BackgroundBrush"]);
            service.Apply(resources, ThemeChoice.Light);
            service.Apply(resources, ThemeChoice.Dark);
            Assert.Same(baseline, resources["BackgroundBrush"]);
        }
        return (resources, discarded);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Amoled")]
    [InlineData("Slate")]
    [InlineData("Light")]
    public void All_themes_expose_readable_save_clip_action_colors(string name)
    {
        var resources = new ResourceDictionary();
        new ThemeResources().Apply(resources, ThemeResources.ThemeChoiceFrom(name));
        foreach (string suffix in new[] { "", "Hover" })
        {
            var background = Assert.IsType<SolidColorBrush>(resources[suffix.Length == 0 ? "AccentBrush" : "AccentHoverBrush"]);
            var foreground = Assert.IsType<SolidColorBrush>(resources[suffix.Length == 0 ? "OnAccentBrush" : "OnAccentHoverBrush"]);
            Assert.True(ThemePalette.Contrast(background.Color, foreground.Color) >= 4.5,
                $"{name} {suffix} Save Clip colors are not readable.");
        }
        Assert.IsType<SolidColorBrush>(resources["DangerBrush"]);
        Assert.IsType<SolidColorBrush>(resources["OnDangerBrush"]);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Amoled")]
    [InlineData("Slate")]
    [InlineData("Light")]
    public void Semantic_controls_share_accessible_colors_without_per_theme_hardcoding(string name)
    {
        var resources = new ResourceDictionary();
        var choice = ThemeResources.ThemeChoiceFrom(name);
        new ThemeResources().Apply(resources, choice);
        Assert.Equal(ThemePalette.Get(choice)[ThemeColor.Surface],
            Assert.IsType<SolidColorBrush>(resources["SurfaceBrush"]).Color);
        Assert.Equal(ThemePalette.Get(choice)[ThemeColor.PrimaryText],
            Assert.IsType<SolidColorBrush>(resources["PrimaryTextBrush"]).Color);
        Assert.Equal(ThemePalette.Get(choice)[ThemeColor.Selection],
            Assert.IsType<SolidColorBrush>(resources["SelectionBrush"]).Color);
        if (choice != ThemeChoice.Dark)
        {
            Assert.Same(resources["AccentBrush"], resources["SliderThumbFill"]);
            Assert.Same(resources["AccentBrush"], resources["SliderTrackValueFill"]);
            Assert.Same(resources["SelectionBrush"], resources["ComboBoxItemBackgroundSelected"]);
            Assert.Same(resources["ElevatedSurfaceBrush"], resources["MenuFlyoutPresenterBackground"]);
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Amoled")]
    [InlineData("Slate")]
    [InlineData("Light")]
    public void Native_mpv_letterbox_uses_the_active_surface_palette(string name)
    {
        if (!AppProfile.IsPersonal || SystemParameters.HighContrast) return;
        var color = Mpv.MpvPlayer.ThemeLetterboxColor(new AppSettings { Theme = name });
        var palette = ThemePalette.Get(ThemeResources.ThemeChoiceFrom(name));
        Assert.Equal(ThemePalette.Hex(palette[ThemeColor.Surface]), color);
    }

    [Fact]
    public void Native_letterbox_falls_back_safely_for_invalid_custom_palettes()
    {
        if (!AppProfile.IsPersonal || SystemParameters.HighContrast) return;
        Assert.Equal(ThemePalette.Hex(ThemePalette.Get(ThemeChoice.Dark)[ThemeColor.Surface]),
            Mpv.MpvPlayer.ThemeLetterboxColor(new AppSettings { Theme = "Custom", CustomThemeJson = "invalid" }));
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Amoled")]
    [InlineData("Slate")]
    [InlineData("Light")]
    [InlineData("Custom")]
    public void Switches_and_internal_navigation_have_consistent_semantic_colors(string name)
    {
        var resources = new ResourceDictionary();
        new ThemeResources().Apply(resources, ThemeResources.ThemeChoiceFrom(name),
            name == "Custom" ? ThemePalette.Get(ThemeChoice.Slate) : null);
        Color ColorFor(string key) => Assert.IsType<SolidColorBrush>(resources[key]).Color;
        Assert.Same(resources["SurfaceBrush"], resources["InternalSidebarBrush"]);
        Assert.True(ThemePalette.Contrast(ColorFor("SwitchOffThumbBrush"), ColorFor("SwitchOffBrush")) >= 3);
        Assert.True(ThemePalette.Contrast(ColorFor("SwitchThumbBrush"), ColorFor("AccentBrush")) >= 3);
        Assert.True(ThemePalette.Contrast(ColorFor("SwitchHoverThumbBrush"), ColorFor("AccentHoverBrush")) >= 3);
        Assert.True(ThemePalette.Contrast(ColorFor("OnDangerBrush"), ColorFor("DangerHoverBrush")) >= 4.5);
        Assert.True(ThemePalette.Contrast(ColorFor("OnDangerBrush"), ColorFor("DangerPressedBrush")) >= 4.5);
        Assert.Same(resources["PrimaryTextBrush"], resources["ButtonForegroundPointerOver"]);
    }

    [Theory]
    [InlineData("{\"MinimizeToTray\":true}", true)]
    [InlineData("{\"MinimizeToTray\":false}", false)]
    [InlineData("{\"MinimizeToTray\":true,\"StartInTray\":false}", false)]
    [InlineData("{\"CloseToTray\":false}", true)]
    public void Legacy_minimize_preference_migrates_only_to_startup(string json, bool expected)
    {
        var settings = AppSettings.Parse(json);
        Assert.Equal(expected, settings.StartInTray);
        Assert.DoesNotContain("MinimizeToTray", settings.ToJson());
        Assert.Equal(settings.CloseToTray, AppSettings.Parse(settings.ToJson()).CloseToTray);
    }

    private static string ExportedPalette()
    {
        using var stream = new MemoryStream();
        new ThemeDocument("Custom sample", ThemePalette.Get(ThemeChoice.Slate)).Write(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Saved_custom_theme_library_migrates_legacy_and_round_trips_multiple_themes()
    {
        if (!AppProfile.IsPersonal) return;
        var legacy = AppSettings.Parse(new AppSettings { Theme = "Custom", CustomThemeJson = ExportedPalette() }.ToJson());
        Assert.Single(legacy.SavedCustomThemes);
        var pink = new ThemeDocument("Pink", ThemePalette.Get(ThemeChoice.Light));
        var purple = new ThemeDocument("Purple", ThemePalette.Get(ThemeChoice.Amoled));
        legacy.SavedCustomThemes = SavedThemes.Put(SavedThemes.Put(legacy.SavedCustomThemes, pink), purple);
        legacy.CustomThemeJson = SavedThemes.Encode(pink);
        var restored = AppSettings.Parse(legacy.ToJson());
        Assert.Equal(3, restored.SavedCustomThemes.Length);
        Assert.Contains(restored.SavedCustomThemes, json => ThemeResources.ReadSaved(json)?.Name == "Purple");
        Assert.Equal("Pink", ThemeResources.ReadSaved(restored.CustomThemeJson)!.Name);
        restored.SavedCustomThemes = SavedThemes.Put(restored.SavedCustomThemes,
            new ThemeDocument("purple", ThemePalette.Get(ThemeChoice.Slate)));
        Assert.Equal(3, restored.SavedCustomThemes.Length);
        Assert.Single(restored.SavedCustomThemes, json => ThemeResources.ReadSaved(json)?.Name == "purple");
        Assert.Equal(3, SavedThemes.Normalize(restored.SavedCustomThemes.Append("invalid"), restored.CustomThemeJson).Length);
    }
    [Fact]
    public void Library_survives_builtin_selection_and_no_active_custom_palette()
    {
        if (!AppProfile.IsPersonal) return;
        var first = new ThemeDocument("First", ThemePalette.Get(ThemeChoice.Slate));
        var second = new ThemeDocument("Second", ThemePalette.Get(ThemeChoice.Amoled));
        var settings = new AppSettings
        {
            Theme = "Custom",
            CustomThemeJson = SavedThemes.Encode(first),
            SavedCustomThemes = SavedThemes.Put(SavedThemes.Put([], first), second),
        };
        settings.Theme = "Dark";
        settings.CustomThemeJson = null;
        var restored = AppSettings.Parse(settings.ToJson());
        Assert.Equal(2, restored.SavedCustomThemes.Length);
        Assert.Contains(restored.SavedCustomThemes, j => ThemeResources.ReadSaved(j)?.Name == "First");
        Assert.Contains(restored.SavedCustomThemes, j => ThemeResources.ReadSaved(j)?.Name == "Second");
    }
}
