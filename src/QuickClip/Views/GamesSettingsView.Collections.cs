using System.Collections.Immutable;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Discovery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GamesSettingsView
{
    private bool _managingCollections;

    private sealed record RecordingChoice(Guid? Id, string Label, bool Automatic = false)
    {
        public override string ToString() => Label;
    }

    private async void GameCollections_Click(object sender, RoutedEventArgs e)
    {
        if (_managingCollections || !PersonalLibrary.Enabled ||
            sender is not Button { DataContext: GameRow row } ||
            Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner) return;
        _managingCollections = true;
        row.ActionError = null;
        var game = row.Entry;
        try
        {
            while (owner.IsVisible)
            {
                var definitions = (await PersonalLibrary.Current.ReadCollectionsAsync(default))
                    .Where(d => d.AppliesTo(game.Id)).ToArray();
                if (!owner.IsVisible) return;
                var preferences = AppSettings.Current.RecordingCollections;
                var preference = preferences?.GetValueOrDefault(game.Id) ?? new GameCollectionPreference();
                var choices = new[] { new RecordingChoice(null, game.DisplayName + " (root)"), new RecordingChoice(null, "Automatic (only explicitly linked executables)", true) }
                    .Concat(definitions.Select(d => new RecordingChoice(d.Id, d.Name + (d.GameId == null ? " (all games)" : "")))).ToArray();
                var active = new ComboBox { ItemsSource = choices, SelectedItem = preference.Automatic ? choices[1] :
                    choices.FirstOrDefault(c => !c.Automatic && c.Id == preference.ManualCollection) ?? choices[0] };
                AutomationProperties.SetName(active, "Save new clips to");
                active.ToolTip = "Choose where new clips appear. The game root is the default.";
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Save new clips to", Margin = new Thickness(0,0,0,4) });
                panel.Children.Add(active);
                panel.Children.Add(new TextBlock { Text = "Optional. Existing clips stay unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,8) });
                var list = new ComboBox { ItemsSource = definitions, DisplayMemberPath = nameof(LibraryCollection.Name), SelectedIndex = -1 };
                AutomationProperties.SetName(list, "Folder to link");
                var extra = new StackPanel();
                extra.Children.Add(new TextBlock { Text = "Link a folder to a game executable", TextWrapping = TextWrapping.Wrap });
                extra.Children.Add(list);
                var buttons = new WrapPanel { Margin = new Thickness(0,6,0,0) };
                var link = new Button { Content = "Link executable", IsEnabled = false, Margin = new Thickness(0,0,6,0) };
                list.SelectionChanged += (_, _) => link.IsEnabled = list.SelectedItem is LibraryCollection;
                var remove = new Button { Content = "Remove link" };
                buttons.Children.Add(link); buttons.Children.Add(remove); extra.Children.Add(buttons);
                var dismiss = new Button();
                string? action = null;
                link.Click += (_, _) => { action = "link"; dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                remove.Click += (_, _) => { action = "remove"; dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                panel.Children.Add(new Expander { Header = "Automatic folder selection", Content = extra, Margin = new Thickness(0,6,0,0) });
                bool saved = ModalDialog.Run(owner, game.DisplayName + " — Clip folder", panel, "Save", "Cancel",
                    safeDefault: true, dismissOnClick: dismiss, validate: () => active.SelectedItem is RecordingChoice);
                if (!owner.IsVisible || !PersonalLibrary.Enabled) break;
                if (saved)
                {
                    if (active.SelectedItem is RecordingChoice choice &&
                        !RecordingCollections.SavePreference(game.Id, preference with { Automatic = choice.Automatic, ManualCollection = choice.Automatic ? null : choice.Id }))
                        throw new IOException("Could not save folder preference.");
                    Status.Text = "Default folder saved.";
                    return;
                }
                if (action == "link" && list.SelectedItem is LibraryCollection selected)
                    LinkCollection(owner, game, selected, preference);
                else if (action == "remove")
                    RemoveCollectionLink(owner, game.Id, preference);
                else break;
            }
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Game collection management failed", ex);
            row.ActionError = ex is LibraryConflictException or ArgumentException or IOException
                ? ex.Message : "Could not manage game collections.";
        }
        finally { _managingCollections = false; }
    }

    private static bool IsVerifiedCollectionExecutable(string path, string gameId)
    {
        var match = GameCatalogRuntime.Match(path, true);
        return match.Kind == CatalogMatchKind.Game && match.Entry?.Id == gameId;
    }

    private static void LinkCollection(Window owner, CatalogEntry game, LibraryCollection collection, GameCollectionPreference preference)
    {
        var paths = game.Executables.Select(CatalogPaths.LocalPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).Where(path => IsVerifiedCollectionExecutable(path, game.Id)).ToArray();
        if (paths.Length == 0)
        {
            ModalDialog.Show(owner, "No uniquely verified gameplay executable is available. Use manual collection selection.", "Link collection");
            return;
        }
        var current = preference.Executables ?? ImmutableDictionary<string, Guid>.Empty;
        var selector = new ComboBox { ItemsSource = paths, SelectedIndex = -1, MaxWidth = 410 };
        AutomationProperties.SetName(selector, "Verified gameplay executable");
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"Link an exact gameplay executable to {collection.Name}. Shared launchers and identical game executables cannot tell game modes apart. This does not change game detection or recording permissions.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(selector);
        if (!ModalDialog.Run(owner, "Link gameplay executable", panel, "Link", "Cancel", safeDefault: true,
            validate: () => selector.SelectedItem is string)) return;
        if (selector.SelectedItem is not string path || !IsVerifiedCollectionExecutable(path, game.Id))
            throw new LibraryConflictException("The executable is no longer verified for this game.");
        var existing = current.Where(p => StringComparer.OrdinalIgnoreCase.Equals(CatalogPaths.LocalPath(p.Key), path)).ToArray();
        if (existing.Any(p => p.Value != collection.Id) && !ModalDialog.AskConfirmation(owner,
            "This executable is linked to another collection. Replace that association? Identical executables cannot distinguish different collections.",
            "Replace executable link", "Replace link")) return;
        var changed = current.RemoveRange(existing.Select(p => p.Key)).SetItem(path, collection.Id);
        if (!RecordingCollections.SavePreference(game.Id, preference with { Executables = changed }))
            throw new IOException("Could not save executable association.");
    }

    private static void RemoveCollectionLink(Window owner, string gameId, GameCollectionPreference preference)
    {
        var current = preference.Executables ?? ImmutableDictionary<string, Guid>.Empty;
        if (current.IsEmpty) { ModalDialog.Show(owner, "No executable links are configured.", "Remove executable link"); return; }
        var selector = new ComboBox { ItemsSource = current.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(), SelectedIndex = 0, MaxWidth = 410 };
        AutomationProperties.SetName(selector, "Executable association to remove");
        if (ModalDialog.Run(owner, "Remove executable link", selector, "Remove link", "Cancel", safeDefault: true,
            validate: () => selector.SelectedItem is string) && selector.SelectedItem is string path &&
            !RecordingCollections.SavePreference(gameId, preference with { Executables = current.Remove(path) }))
            throw new IOException("Could not remove executable association.");
    }
}
