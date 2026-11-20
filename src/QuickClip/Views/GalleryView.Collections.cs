using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using QuickClip.Discovery;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    internal sealed record CollectionScope(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record ManagedLabel(LibraryCollection? Collection, LibraryTagDefinition? Tag, string Label);
    private async void CollectionsManager_Click(object sender, RoutedEventArgs e)
    {
        if (!PersonalLibrary.Enabled || _loading || _released || _editingAnnotations ||
            Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner ||
            !await FlushAnnotationsAsync() || _released || _loading || _editingAnnotations) return;
        _editingAnnotations = true;
        try
        {
            while (!_released)
            {
                var definitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
                if (_released) return;
                var tags = await PersonalLibrary.Current.ReadTagDefinitionsAsync(_lifetime.Token);
                if (_released) return;
                var scopes = CollectionScopes(definitions, tags);
                var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == _folderSelection.Game.GameId) ?? scopes[0] };
                var list = new ListBox { MinHeight = 80, MaxHeight = 230 };
                var action = new ComboBox { ItemsSource = new[] { "Create label", "Edit selected label", "Delete selected label", "Rename or merge global tag", "Recording collection for this game" }, SelectedIndex = 0 };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Collections are optional virtual homes for clips. Ordinary tags remain independent. No files are moved.", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "Scope", Margin = new Thickness(0, 10, 0, 4) });
                panel.Children.Add(scope);
                panel.Children.Add(list);
                panel.Children.Add(action);
                AutomationProperties.SetName(scope, "Collection scope");
                AutomationProperties.SetName(list, "Collections and tags");
                AutomationProperties.SetName(action, "Management action");
                void Populate()
                {
                    var selected = (scope.SelectedItem as CollectionScope)?.Id;
                    list.ItemsSource = definitions.Where(d => d.GameId == selected).Select(d =>
                        new ManagedLabel(d, null, d.Name + "  [Collection]" + (d.FolderLeaf == null ? "" : " / " + d.FolderLeaf)))
                        .Concat(tags.Where(t => t.GameId == selected).Select(t => new ManagedLabel(null, t, t.Name + "  [Tag]")))
                        .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToArray();
                    list.DisplayMemberPath = nameof(ManagedLabel.Label);
                    list.SelectedIndex = 0;
                }
                scope.SelectionChanged += (_, _) => Populate();
                Populate();
                if (!ModalDialog.Run(owner, "Collections & Tags", panel, "Continue", "Close", safeDefault: true,
                    validate: () => action.SelectedIndex switch { 0 or 3 => true, 4 => (scope.SelectedItem as CollectionScope)?.Id != null, _ => list.SelectedItem is ManagedLabel })) break;
                if (_released) return;
                string? selectedScope = (scope.SelectedItem as CollectionScope)?.Id;
                if (action.SelectedIndex == 3)
                {
                    _editingAnnotations = false;
                    // Leave this modal loop and enter the existing bounded global
                    // merge workflow after its finally has released ownership.
                    _ = Dispatcher.BeginInvoke(() => LibraryTags_Click(sender, e));
                    return;
                }
                if (action.SelectedIndex == 4)
                {
                    if (selectedScope != null)
                        ChooseRecordingCollection(owner, selectedScope, definitions);
                    continue;
                }
                var existing = list.SelectedItem as ManagedLabel;
                if (action.SelectedIndex == 2 && existing != null)
                {
                    string label = existing.Collection?.Name ?? existing.Tag!.Name;
                    if (ModalDialog.AskConfirmation(owner,
                        existing.Collection != null
                            ? $"Delete {label}? Its recordings become Uncategorized. Files and ordinary tags stay unchanged."
                            : $"Delete {label} and remove that tag from all its recordings, including offline ones? Files and other labels stay unchanged.",
                        "Delete label", "Delete label"))
                    {
                        if (existing.Collection is { } collection)
                            await PersonalLibrary.Current.DeleteCollectionAsync(collection, _lifetime.Token);
                        else await PersonalLibrary.Current.DeleteTagDefinitionAsync(existing.Tag!, _lifetime.Token);
                    }
                }
                else if (action.SelectedIndex == 1 && existing != null)
                {
                    if (existing.Collection is { } collection)
                    {
                        var edited = EditCollection(owner, scopes, collection, selectedScope);
                        if (edited != null) await PersonalLibrary.Current.UpdateCollectionAsync(collection, edited, _lifetime.Token);
                    }
                    else
                    {
                        var edited = EditTag(owner, scopes, existing.Tag, selectedScope);
                        if (edited != null) await PersonalLibrary.Current.UpdateTagDefinitionAsync(existing.Tag!, edited, _lifetime.Token);
                    }
                }
                else if (action.SelectedIndex == 0)
                {
                    var kind = new ComboBox { ItemsSource = new[] { "Ordinary tag", "Collection (optional folder)" }, SelectedIndex = 0 };
                    var typePanel = new StackPanel();
                    typePanel.Children.Add(new TextBlock { Text = "Tags are independent labels. A clip can belong to one collection, with an optional physical folder preference.", TextWrapping = TextWrapping.Wrap });
                    typePanel.Children.Add(kind);
                    if (!ModalDialog.Run(owner, "Create label", typePanel, "Continue", "Cancel", safeDefault: true)) continue;
                    if (kind.SelectedIndex == 0)
                    {
                        var tag = EditTag(owner, scopes, null, selectedScope);
                        if (tag != null) await PersonalLibrary.Current.CreateTagDefinitionAsync(tag, _lifetime.Token);
                    }
                    else
                    {
                        var collection = EditCollection(owner, scopes, null, selectedScope);
                        if (collection != null) await PersonalLibrary.Current.CreateCollectionAsync(collection, _lifetime.Token);
                    }
                }
                if (!_released) await ReloadAsync(rereadIndex: true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Collection management failed", ex);
            if (!_released) ShowStatus(ex is LibraryConflictException or ArgumentException ? ex.Message : "Could not manage collections.");
        }
        finally { _editingAnnotations = false; }
    }

    private sealed record CollectionChoice(Guid? Id, string Label);

    private CollectionScope[] CollectionScopes(LibraryCollection[] definitions, LibraryTagDefinition[]? tags = null)
    {
        var games = GameCatalogRuntime.Rows(AppSettings.Current.Games ?? new())
            .Select(g => new CollectionScope(g.Id, g.DisplayName))
            .Concat(_clips.Where(c => c.Details.GameId != null)
                .Select(c => new CollectionScope(c.Details.GameId, c.Details.Game)))
            .Concat(definitions.Where(d => d.GameId != null).Select(d => new CollectionScope(d.GameId, d.GameId!)))
            .Concat((tags ?? []).Where(t => t.GameId != null).Select(t => new CollectionScope(t.GameId, t.GameId!)))
            .DistinctBy(g => g.Id).OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase);
        return [new(null, "All games"), .. games];
    }

    internal static LibraryTagDefinition? EditTag(Window owner, CollectionScope[] scopes, LibraryTagDefinition? original, string? initialScope)
    {
        var name = new TextBox { Text = original?.Name ?? "", MaxLength = ClipDetails.MaxTagLength };
        var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == (original == null ? initialScope : original.GameId)) ?? scopes[0] };
        var color = new ComboBox { ItemsSource = TagPalette.Choices, SelectedItem = TagPalette.Choices.FirstOrDefault(c => c.Hex == original?.Color) ?? TagPalette.Choices[0] };
        scope.ToolTip = "A game-specific tag is available only for clips associated with that game.";
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        void Field(string label, Control input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0,8,0,4) });
            AutomationProperties.SetName(input, label);
            panel.Children.Add(input);
        }
        Field("Name", name);
        Field("Available for", scope);
        Field("Color", color);
        panel.Children.Add(error);
        LibraryTagDefinition? result = null;
        bool accepted = ModalDialog.Run(owner, original == null ? "Create ordinary tag" : "Edit ordinary tag", panel, "Save", "Cancel", safeDefault: true,
            validate: () =>
            {
                try
                {
                    result = new LibraryTagDefinition(original?.Id ?? 0, name.Text, (scope.SelectedItem as CollectionScope)?.Id,
                        (color.SelectedItem as TagColorChoice)?.Hex).Normalize();
                    return true;
                }
                catch (ArgumentException ex) { error.Text = ex.Message; return false; }
            });
        return accepted ? result : null;
    }

    private static void ChooseRecordingCollection(Window owner, string gameId, LibraryCollection[] definitions)
    {
        var prior = AppSettings.Current.RecordingCollections?.GetValueOrDefault(gameId) ?? new GameCollectionPreference();
        var choices = new[] { new CollectionChoice(null, "None") }.Concat(definitions.Where(d => d.AppliesTo(gameId))
            .Select(d => new CollectionChoice(d.Id, d.Name + (d.GameId == null ? " (all games)" : "")))).ToArray();
        var combo = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(CollectionChoice.Label),
            SelectedItem = prior.Automatic ? null : choices.FirstOrDefault(c => c.Id == prior.ManualCollection) ?? choices[0] };
        combo.ToolTip = "Manual choice overrides executable linking. Configure automatic linking in Games.";
        AutomationProperties.SetName(combo, "Active recording collection");
        if (ModalDialog.Run(owner, "Recording collection for subsequent saves", combo, "Save", "Cancel", safeDefault: true,
            validate: () => combo.SelectedItem is CollectionChoice) && combo.SelectedItem is CollectionChoice selected &&
            !RecordingCollections.SavePreference(gameId, prior with { Automatic = false, ManualCollection = selected.Id }))
            throw new IOException("Could not save recording collection.");
    }

    internal static LibraryCollection? EditCollection(Window owner, CollectionScope[] scopes, LibraryCollection? original, string? initialScope)
    {
        var name = new TextBox { Text = original?.Name ?? "", MaxLength = ClipDetails.MaxTagLength };
        var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == (original == null ? initialScope : original.GameId)) ?? scopes[0] };
        var color = new ComboBox { ItemsSource = TagPalette.Choices, SelectedItem = TagPalette.Choices.FirstOrDefault(c => c.Hex == original?.Color) ?? TagPalette.Choices[0] };
        var folder = new CheckBox { Content = "Remember optional subfolder preference", IsChecked = original?.FolderLeaf != null };
        var leaf = new TextBox { Text = original?.FolderLeaf ?? "", MaxLength = 64, IsEnabled = folder.IsChecked == true };
        folder.Checked += (_, _) => { leaf.IsEnabled = true; if (leaf.Text.Length == 0) leaf.Text = name.Text.Trim(); };
        folder.Unchecked += (_, _) => leaf.IsEnabled = false;
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        void Field(string label, Control input)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
            AutomationProperties.SetName(input, label);
            panel.Children.Add(input);
        }
        Field("Name", name);
        Field("Available for", scope);
        Field("Color", color);
        panel.Children.Add(folder);
        Field("Folder name (inactive until physical organization is supported)", leaf);
        panel.Children.Add(error);
        LibraryCollection? result = null;
        bool accepted = ModalDialog.Run(owner, original == null ? "Create collection" : "Edit collection", panel, "Save", "Cancel", safeDefault: true,
            validate: () =>
            {
                try
                {
                    result = new LibraryCollection(original?.Id ?? Guid.NewGuid(), name.Text,
                        (scope.SelectedItem as CollectionScope)?.Id, (color.SelectedItem as TagColorChoice)?.Hex,
                        folder.IsChecked == true ? leaf.Text : null).Normalize();
                    return true;
                }
                catch (ArgumentException ex) { error.Text = ex.Message; return false; }
            });
        return accepted ? result : null;
    }
    private async void AssignCollection_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await AssignCollectionToAsync(item.IsSelected ? CurrentClips.Where(c => c.IsSelected).ToArray() : [item]);
    }

    private async Task AssignCollectionToAsync(IReadOnlyList<ClipItem> clips)
    {
        if (!PersonalLibrary.Enabled || _released || _loading || _editingAnnotations || clips.Count == 0 ||
            Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner ||
            !await FlushAnnotationsAsync() || _released || _loading || _editingAnnotations) return;
        if (clips.Count > 256 || clips.Any(c => c.Details.IndexId == null || !IsAvailable(c)))
        {
            ShowStatus("Select up to 256 available indexed recordings.");
            return;
        }
        _editingAnnotations = true;
        _hoverPreview.Stop();
        GalleryDetailsEditor.IsEnabled = false;
        try
        {
            var targets = clips.Select(ClipAnnotationTarget.For).ToArray();
            var definitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
            if (_released) return;
            var scopes = CollectionScopes(definitions);
            var applicable = definitions.Where(d => targets.All(t =>
                d.GameId == null || t.Details.GameId == null || t.Details.GameId == d.GameId))
                .Select(d => new CollectionChoice(d.Id, d.Name + " — " +
                    (scopes.FirstOrDefault(s => s.Id == d.GameId)?.Label ?? d.GameId ?? "All games"))).ToArray();
            var options = new[] { new CollectionChoice(null, "None / Uncategorized") }.Concat(applicable).ToArray();
            var list = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(CollectionChoice.Label), SelectedIndex = -1 };
            if (targets.Select(t => t.Details.Collection?.Id).Distinct().Count() == 1)
                list.SelectedItem = options.FirstOrDefault(o => o.Id == targets[0].Details.Collection?.Id);
            AutomationProperties.SetName(list, "Assign collection");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Choose one collection for {targets.Length} recording(s). Ordinary tags and files are unchanged.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) });
            panel.Children.Add(list);
            if (!ModalDialog.Run(owner, "Assign collection", panel, "Apply", "Cancel", safeDefault: true,
                validate: () => list.SelectedItem is CollectionChoice) || _released ||
                list.SelectedItem is not CollectionChoice selected) return;
            var definition = definitions.FirstOrDefault(d => d.Id == selected.Id);
            string? associate = null;
            if (definition?.GameId is { } game && targets.Any(t => t.Details.GameId == null))
            {
                int count = targets.Count(t => t.Details.GameId == null);
                string label = scopes.FirstOrDefault(s => s.Id == game)?.Label ?? game;
                if (!ModalDialog.AskConfirmation(owner,
                    $"{count} selected recording(s) have no verified game association. Associate them with {label} for library organization? This does not change capture permissions.",
                    "Associate older recordings", "Associate and assign")) return;
                associate = game;
            }
            if (_released) return;
            var job = PersonalLibrary.Current.AssignCollectionsAsync(targets, selected.Id, associate, _lifetime.Token);
            _annotationJob = job;
            int changed = await job;
            if (_released) return;
            if (selected.Id != null)
                foreach (var t in targets)
                    _expandedGames.Add(t.Details.GameId == null && associate != null
                        ? new GalleryFolderKey(associate, null) : GalleryFolderKey.For(t.Details));
            await ReloadAsync(rereadIndex: true);
            ShowStatus($"Updated collection for {changed:N0} recording(s).");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Collection assignment failed", ex);
            if (!_released) ShowStatus(ex is LibraryConflictException or ArgumentException ? ex.Message : "Could not assign collection.");
        }
        finally
        {
            _annotationJob = null;
            _editingAnnotations = false;
            GalleryDetailsEditor.IsEnabled = true;
        }
    }

    private static ClipItem? FindCollectionClip(DependencyObject element)
    {
        for (DependencyObject? current = element; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: ClipItem clip }) return clip;
        return null;
    }
}
