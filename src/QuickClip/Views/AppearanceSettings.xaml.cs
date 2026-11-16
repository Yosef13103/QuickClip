using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Themes;

namespace QuickClip.Views;

public partial class AppearanceSettings : UserControl
{
    private sealed record Option(ThemeChoice Choice, string Label, string? Json = null);
    private bool _loading;
    private readonly ObservableCollection<Option> _options = [];

    public AppearanceSettings()
    {
        InitializeComponent();
        ThemePicker.ItemsSource = _options;
        if (!AppProfile.IsPersonal || Application.Current is not App)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        CustomEditor.Saved += ApplySavedTheme;
        Loaded += (_, _) => RefreshSelection();
    }

    private void RefreshSelection() => RefreshSelection(AppSettings.Current);

    internal void RefreshSelection(AppSettings settings)
    {
        bool previousLoading = _loading;
        _loading = true;
        try
        {
            var options = new List<Option>
            {
                new(ThemeChoice.Dark, "QuickClip Dark"), new(ThemeChoice.Amoled, "AMOLED"),
                new(ThemeChoice.Slate, "Slate"), new(ThemeChoice.Light, "Light"),
            };
            foreach (var json in SavedThemes.Normalize(settings.SavedCustomThemes, settings.CustomThemeJson))
                if (ThemeResources.ReadSaved(json) is { } theme)
                    options.Add(new(ThemeChoice.Custom, theme.Name + " (custom)", json));
            for (int i = 0; i < options.Count; i++)
            {
                if (i == _options.Count) _options.Add(options[i]);
                else if (_options[i] != options[i]) _options[i] = options[i];
            }
            while (_options.Count > options.Count) _options.RemoveAt(_options.Count - 1);
            var selected = ThemeResources.ThemeChoiceFrom(settings.Theme);
            var active = ThemeResources.ReadSaved(settings.CustomThemeJson);
            ThemePicker.SelectedItem = selected == ThemeChoice.Custom
                ? _options.FirstOrDefault(option => option.Choice == selected &&
                    string.Equals(ThemeResources.ReadSaved(option.Json)?.Name, active?.Name, StringComparison.OrdinalIgnoreCase)) ?? _options[0]
                : _options.FirstOrDefault(option => option.Choice == selected) ?? _options[0];
            RenameThemeButton.IsEnabled = RemoveThemeButton.IsEnabled =
                ThemePicker.SelectedItem is Option { Choice: ThemeChoice.Custom };
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
        // ThemeMode can briefly unload the picker during Fluent resource invalidation.
        // Programmatic selection is already guarded by RefreshSelection.
        if (_loading || !ReferenceEquals(e.OriginalSource, ThemePicker) ||
            Application.Current is not App app || ThemePicker.SelectedItem is not Option option)
            return;
        var settings = AppSettings.Current;
        if (ThemeResources.ThemeChoiceFrom(settings.Theme) == option.Choice &&
            (option.Choice != ThemeChoice.Custom || settings.CustomThemeJson == option.Json)) return;
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
        using var operation = Log.Begin(DiagnosticCategory.Settings, "Theme selection persistence");
        if (settings.TrySave())
        {
            operation?.Complete();
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
    private void RenameTheme_Click(object sender, RoutedEventArgs e)
    {
        if (ThemePicker.SelectedItem is not Option { Choice: ThemeChoice.Custom } option ||
            ThemeResources.ReadSaved(option.Json) is not { } theme ||
            TextPrompt.Ask(Window.GetWindow(this), "Theme name", theme.Name, "Rename theme") is not { } name) return;
        if (RenameTheme(theme, name) is { } error)
            ModalDialog.Show(Window.GetWindow(this), error, "Could not rename theme");
    }

    internal string? RenameTheme(ThemeDocument theme, string name)
    {
        if (Application.Current is not App app) return "Theme settings are unavailable.";
        var settings = AppSettings.Current;
        string? previous = settings.CustomThemeJson;
        var library = settings.SavedCustomThemes;
        var renamed = theme with { Name = name.Trim() };
        try { renamed.Validate(); }
        catch (InvalidDataException ex) { return ex.Message; }
        var remaining = SavedThemes.Normalize(library, previous).Where(json =>
            !string.Equals(ThemeResources.ReadSaved(json)?.Name, theme.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remaining.Any(json => string.Equals(ThemeResources.ReadSaved(json)?.Name, renamed.Name, StringComparison.OrdinalIgnoreCase)))
            return "A saved theme already uses that name.";

        settings.CustomThemeJson = SavedThemes.Encode(renamed);
        settings.SavedCustomThemes = SavedThemes.Put(remaining, renamed);
        using var operation = Log.Begin(DiagnosticCategory.Settings, "Theme rename persistence");
        if (!settings.TrySave())
        {
            settings.CustomThemeJson = previous;
            settings.SavedCustomThemes = library;
            return "QuickClip could not save the theme name.";
        }
        operation?.Complete();
        ApplyAndRefresh(app, settings);
        CustomEditor.LoadDraft(renamed);
        return null;
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
        using var operation = Log.Begin(DiagnosticCategory.Settings, "Theme removal persistence");
        if (!settings.TrySave())
        {
            settings.SavedCustomThemes = old;
            settings.CustomThemeJson = json;
            settings.Theme = choice;
            ModalDialog.Show(Window.GetWindow(this), "Could not remove the saved theme.", "Settings");
            return;
        }
        operation?.Complete();
        ApplyAndRefresh(app, settings);
    }
}
