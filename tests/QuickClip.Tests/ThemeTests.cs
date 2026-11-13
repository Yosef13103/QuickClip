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
            valid.Replace("#202936", "https://example.invalid/theme"),
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

    private static string ExportedPalette()
    {
        using var stream = new MemoryStream();
        new ThemeDocument("Custom sample", ThemePalette.Get(ThemeChoice.Slate)).Write(stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
