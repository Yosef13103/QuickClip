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
                var choices = new[] { new RecordingChoice(null, "None"), new RecordingChoice(null, "Automatic (linked game executable)", true) }
                    .Concat(definitions.Select(d => new RecordingChoice(d.Id, d.Name + (d.GameId == null ? " (all games)" : "")))).ToArray();
                var active = new ComboBox { ItemsSource = choices, SelectedItem = preference.Automatic ? choices[1] :
                    choices.FirstOrDefault(c => !c.Automatic && c.Id == preference.ManualCollection) ?? choices[0] };
                var list = new ListBox { ItemsSource = definitions, DisplayMemberPath = nameof(LibraryCollection.Name), SelectedIndex = 0, MinHeight = 50, MaxHeight = 150 };
                var action = new ComboBox { ItemsSource = new[] { "Save recording selection", "Create collection", "Edit selected collection", "Delete selected collection", "Link gameplay executable", "Remove executable link", "Create game-specific tag" }, SelectedIndex = 0 };
                AutomationProperties.SetName(active, "Recording collection");
                AutomationProperties.SetName(list, "Available collections");
                AutomationProperties.SetName(action, "Collection action");
                active.ToolTip = "Manual selection applies to subsequent saves. Automatic requires an exact linked gameplay executable; unmatched recordings remain Uncategorized.";
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Choose a collection for new recordings of this game. Existing clips and recording permissions do not change.", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "Recording collection", Margin = new Thickness(0,10,0,4) });
                panel.Children.Add(active);
                panel.Children.Add(new TextBlock { Text = "Available collections", Margin = new Thickness(0,10,0,4) });
                panel.Children.Add(list);
                panel.Children.Add(new TextBlock { Text = "Action", Margin = new Thickness(0,10,0,4) });
                panel.Children.Add(action);
                if (!ModalDialog.Run(owner, game.DisplayName + " — Collections", panel, "Continue", "Close", safeDefault: true,
                    validate: () => action.SelectedIndex is 0 or 1 or 5 or 6 || list.SelectedItem is LibraryCollection)) break;
                if (!owner.IsVisible || !PersonalLibrary.Enabled) break;
                var selected = list.SelectedItem as LibraryCollection;
                switch (action.SelectedIndex)
                {
                    case 0:
                        if (active.SelectedItem is RecordingChoice choice &&
                            !RecordingCollections.SavePreference(game.Id, preference with { Automatic = choice.Automatic, ManualCollection = choice.Id }))
                            throw new IOException("Could not save recording collection selection.");
                        Status.Text = "Recording collection selection saved.";
                        return;
                    case 1:
                    case 2:
                        var original = action.SelectedIndex == 2 ? selected : null;
                        var scopes = new GalleryView.CollectionScope[] { new(game.Id, game.DisplayName), new(null, "All games") };
                        var edited = GalleryView.EditCollection(owner, scopes, original, game.Id);
                        if (edited == null) break;
                        if (original == null) await PersonalLibrary.Current.CreateCollectionAsync(edited, default);
                        else await PersonalLibrary.Current.UpdateCollectionAsync(original, edited, default);
                        break;
                    case 3:
                        if (selected != null && ModalDialog.AskConfirmation(owner,
                            $"Delete {selected.Name}? Its recordings become Uncategorized. No files or ordinary tags are deleted.",
                            "Delete collection", "Delete collection"))
                            await PersonalLibrary.Current.DeleteCollectionAsync(selected, default);
                        break;
                    case 4:
                        if (selected != null)
                            LinkCollection(owner, game, selected, preference);
                        break;
                    case 5:
                        RemoveCollectionLink(owner, game.Id, preference);
                        break;
                    case 6:
                        var tag = GalleryView.EditTag(owner, [new(game.Id, game.DisplayName), new(null, "All games")], null, game.Id);
                        if (tag != null) await PersonalLibrary.Current.CreateTagDefinitionAsync(tag, default);
                        break;
                }
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
        var selector = new ComboBox { ItemsSource = paths, SelectedIndex = 0, MaxWidth = 410 };
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
