using System.Windows;
using System.Windows.Controls;
using QuickClip.Themes;

namespace QuickClip.Views;

public partial class AppearanceSettings : UserControl
{
    private sealed record Option(ThemeChoice Choice, string Label, string? Json = null);
    private bool _loading;

    public AppearanceSettings()
    {
        InitializeComponent();
        if (!AppProfile.IsPersonal || Application.Current is not App)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        CustomEditor.Saved += ApplySavedTheme;
        Loaded += (_, _) => RefreshSelection();
        ThemePicker.DropDownOpened += (_, _) => RefreshSelection();
    }

    private void RefreshSelection()
    {
        bool previousLoading = _loading;
        _loading = true;
        try
        {
            var settings = AppSettings.Current;
            var options = new List<Option>
            {
                new(ThemeChoice.Dark, "QuickClip Dark"), new(ThemeChoice.Amoled, "AMOLED"),
                new(ThemeChoice.Slate, "Slate"), new(ThemeChoice.Light, "Light"),
            };
            foreach (var json in SavedThemes.Normalize(settings.SavedCustomThemes, settings.CustomThemeJson))
                if (ThemeResources.ReadSaved(json) is { } theme)
                    options.Add(new(ThemeChoice.Custom, theme.Name + " (custom)", json));
            if (ThemePicker.ItemsSource is not IEnumerable<Option> current || !current.SequenceEqual(options))
                ThemePicker.ItemsSource = options;
            else options = current.ToList();
            var selected = ThemeResources.ThemeChoiceFrom(settings.Theme);
            var active = ThemeResources.ReadSaved(settings.CustomThemeJson);
            ThemePicker.SelectedItem = selected == ThemeChoice.Custom
                ? options.FirstOrDefault(option => option.Choice == selected &&
                    string.Equals(ThemeResources.ReadSaved(option.Json)?.Name, active?.Name, StringComparison.OrdinalIgnoreCase)) ?? options[0]
                : options.FirstOrDefault(option => option.Choice == selected) ?? options[0];
            RemoveThemeButton.IsEnabled = ThemePicker.SelectedItem is Option { Choice: ThemeChoice.Custom };
        }
        finally { _loading = previousLoading; }
    }

    private void ApplySavedTheme()
    {
        if (Application.Current is not App app) return;
        ApplyAndRefresh(app, AppSettings.Current);
    }

    // ThemeMode can invalidate Fluent control templates and raise selection events.
    // Treat the whole apply/refresh as one guarded transition, including import/removal.
    private void ApplyAndRefresh(App app, AppSettings settings)
    {
        bool previousLoading = _loading;
        _loading = true;
        try
        {
            app.Themes.Apply(app, settings);
            RefreshSelection();
        }
        finally { _loading = previousLoading; }
    }

    private void ThemePicker_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || Application.Current is not App app || ThemePicker.SelectedItem is not Option option)
            return;
        var settings = AppSettings.Current;
        if (option.Choice == ThemeChoice.Custom && (ThemeResources.ReadSaved(option.Json) is not { } custom ||
            ThemePalette.ReadabilityWarnings(custom.Palette).Any()))
        {
            RefreshSelection();
            ModalDialog.Show(Window.GetWindow(this), "Save a readable palette under Custom palette first.", "Custom theme");
            return;
        }
        string? previous = settings.Theme, previousJson = settings.CustomThemeJson;
        var previousLibrary = settings.SavedCustomThemes;
        // The saved collection is independent of the selected presentation.
        settings.SavedCustomThemes = SavedThemes.Normalize(previousLibrary, previousJson);
        settings.Theme = option.Choice.ToString();
        if (option.Choice == ThemeChoice.Custom)
        {
            settings.CustomThemeJson = option.Json;
            settings.SavedCustomThemes = SavedThemes.Normalize(settings.SavedCustomThemes, option.Json);
        }
        if (settings.TrySave())
        {
            ApplyAndRefresh(app, settings);
            if (option.Choice == ThemeChoice.Custom && ThemeResources.ReadSaved(option.Json) is { } theme)
                CustomEditor.LoadDraft(theme);
            RemoveThemeButton.IsEnabled = option.Choice == ThemeChoice.Custom;
        }
        else
        {
            settings.Theme = previous;
            settings.CustomThemeJson = previousJson;
            settings.SavedCustomThemes = previousLibrary;
            RefreshSelection();
            ModalDialog.Show(Window.GetWindow(this), "QuickClip could not save the selected theme.",
                "Settings could not be saved", image: MessageBoxImage.Error);
        }
    }
    private void RemoveTheme_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app || ThemePicker.SelectedItem is not Option { Choice: ThemeChoice.Custom } option ||
            !ModalDialog.AskConfirmation(Window.GetWindow(this), $"Remove {option.Label} from your saved themes?", "Remove theme", "Remove")) return;
        var settings = AppSettings.Current;
        var old = settings.SavedCustomThemes;
        string? json = settings.CustomThemeJson, choice = settings.Theme;
        var name = ThemeResources.ReadSaved(option.Json)?.Name;
        settings.SavedCustomThemes = SavedThemes.Normalize(old, json).Where(item =>
            !string.Equals(ThemeResources.ReadSaved(item)?.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        settings.CustomThemeJson = null;
        settings.Theme = nameof(ThemeChoice.Dark);
        if (!settings.TrySave())
        {
            settings.SavedCustomThemes = old;
            settings.CustomThemeJson = json;
            settings.Theme = choice;
            ModalDialog.Show(Window.GetWindow(this), "Could not remove the saved theme.", "Settings");
            return;
        }
        ApplyAndRefresh(app, settings);
    }
}
