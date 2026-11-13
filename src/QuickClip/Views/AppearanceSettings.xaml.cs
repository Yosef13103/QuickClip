using System.Windows;
using System.Windows.Controls;
using QuickClip.Themes;

namespace QuickClip.Views;

public partial class AppearanceSettings : UserControl
{
    private sealed record Option(ThemeChoice Choice, string Label);
    private bool _loading;

    public AppearanceSettings()
    {
        InitializeComponent();
        if (!AppProfile.IsPersonal || Application.Current is not App)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        ThemePicker.ItemsSource = new Option[]
        {
            new(ThemeChoice.Dark, "QuickClip Dark"),
            new(ThemeChoice.Amoled, "AMOLED"),
            new(ThemeChoice.Slate, "Slate"),
            new(ThemeChoice.Light, "Light"),
            new(ThemeChoice.Custom, "Custom"),
        };
        CustomEditor.Saved += RefreshSelection;
        Loaded += (_, _) => RefreshSelection();
    }

    private void RefreshSelection()
    {
        _loading = true;
        try
        {
            var options = ThemePicker.Items.OfType<Option>();
            var settings = AppSettings.Current;
            var selected = ThemeResources.ThemeChoiceFrom(settings.Theme);
            if (selected == ThemeChoice.Custom && (ThemeResources.ReadSaved(settings.CustomThemeJson) is not { } custom ||
                ThemePalette.ReadabilityWarnings(custom.Palette).Any()))
                selected = ThemeChoice.Dark;
            ThemePicker.SelectedItem = options.FirstOrDefault(option => option.Choice == selected) ?? options.First();
        }
        finally { _loading = false; }
    }

    private void ThemePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || Application.Current is not App app || ThemePicker.SelectedItem is not Option option)
            return;
        var settings = AppSettings.Current;
        if (option.Choice == ThemeChoice.Custom && (ThemeResources.ReadSaved(settings.CustomThemeJson) is not { } custom ||
            ThemePalette.ReadabilityWarnings(custom.Palette).Any()))
        {
            RefreshSelection();
            ModalDialog.Show(Window.GetWindow(this), "Save a readable palette under Custom palette first.", "Custom theme");
            return;
        }
        string? previous = settings.Theme;
        settings.Theme = option.Choice.ToString();
        if (settings.TrySave()) app.Themes.Apply(app, settings);
        else
        {
            settings.Theme = previous;
            RefreshSelection();
            ModalDialog.Show(Window.GetWindow(this), "QuickClip could not save the selected theme.",
                "Settings could not be saved", image: MessageBoxImage.Error);
        }
    }
}
