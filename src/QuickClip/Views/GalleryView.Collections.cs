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
            string? selectedScope = _folderSelection.Game.GameId;
            while (!_released)
            {
                var definitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
                var tags = await PersonalLibrary.Current.ReadTagDefinitionsAsync(_lifetime.Token);
                if (_released || !owner.IsVisible) return;
                var scopes = CollectionScopes(definitions, tags);
                var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == selectedScope) ?? scopes[0] };
                AutomationProperties.SetName(scope, "Show labels for");
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Tags help you find clips. Folders group them.", TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(new TextBlock { Text = "Available for", Margin = new Thickness(0, 12, 0, 4) });
                panel.Children.Add(scope);
                var rows = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
                panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 310, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                var dismiss = new Button();
                ManagedLabel? existing = null;
                string? action = null;
                void Select(string next, ManagedLabel? label = null)
                {
                    action = next; existing = label;
                    dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Button MakeButton(string text, string hint, Action click)
                {
                    var button = new Button { Content = text, ToolTip = hint, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(4, 0, 0, 0) };
                    AutomationProperties.SetName(button, hint);
                    button.Click += (_, _) => click();
                    return button;
                }
                void Populate()
                {
                    selectedScope = (scope.SelectedItem as CollectionScope)?.Id;
                    rows.Children.Clear();
                    var labels = definitions.Where(d => d.GameId == selectedScope).Select(d => new ManagedLabel(d, null, d.Name))
                        .Concat(tags.Where(t => t.GameId == selectedScope).Select(t => new ManagedLabel(null, t, t.Name)))
                        .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (labels.Length == 0)
                        rows.Children.Add(new TextBlock { Text = "No labels yet. Create one below.", Margin = new Thickness(0, 12, 0, 12) });
                    foreach (var label in labels)
                    {
                        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
                        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(buttons, Dock.Right);
                        var edit = MakeButton("\uE70F", "Edit " + label.Label, () => Select("edit", label));
                        edit.SetResourceReference(Control.FontFamilyProperty, "IconFont");
                        edit.MinWidth = 36;
                        buttons.Children.Add(edit);
                        var delete = MakeButton("\uE74D", "Delete " + label.Label, () => Select("delete", label));
                        delete.SetResourceReference(Control.FontFamilyProperty, "IconFont");
                        delete.MinWidth = 36;
                        buttons.Children.Add(delete);
                        if (label.Tag != null)
                            buttons.Children.Add(MakeButton("Make folder", "Turn " + label.Label + " into a Gallery folder", () => Select("convert", label)));
                        row.Children.Add(buttons);
                        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                        var kindIcon = new TextBlock { Text = label.Collection != null ? "\uE838" : "\uE8EC", Margin = new Thickness(0,0,7,0), VerticalAlignment = VerticalAlignment.Center };
                        kindIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
                        DockPanel.SetDock(kindIcon, Dock.Left);
                        row.Children.Add(kindIcon);
                        var title = new TextBlock { Text = label.Label, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold, ToolTip = label.Label };
                        string? hex = label.Collection?.Color ?? label.Tag?.Color;
                        if (hex != null)
                        {
                            var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
                            var swatch = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(0,0,6,0) };
                            var heading = new DockPanel(); DockPanel.SetDock(swatch, Dock.Left); heading.Children.Add(swatch); heading.Children.Add(title); name.Children.Add(heading);
                        }
                        else name.Children.Add(title);
                        var kindText = new TextBlock { Text = label.Collection != null ? "Folder" : "Tag" };
                        kindText.SetResourceReference(TextBlock.FontSizeProperty, "QuickClipText12");
                        name.Children.Add(kindText);
                        row.Children.Add(name);
                        rows.Children.Add(row);
                    }
                }
                scope.SelectionChanged += (_, _) => Populate();
                Populate();
                panel.Children.Add(MakeButton("+ Create label", "Create label", () => Select("create")));
                panel.Children.Add(MakeButton("Merge tags...", "Rename or merge global tags", () => Select("merge")));
                ModalDialog.Run(owner, "Manage labels", panel, "Close", null, safeDefault: true, wide: true, dismissOnClick: dismiss);
                if (_released || !owner.IsVisible || action == null) break;
                if (action == "merge")
                {
                    _editingAnnotations = false;
                    _ = Dispatcher.BeginInvoke(() => LibraryTags_Click(sender, e));
                    return;
                }
                try
                {
                    if (action == "delete" && existing != null)
                    {
                        if (ModalDialog.AskConfirmation(owner, $"Delete {existing.Label}? Your video files stay.", "Delete label", "Delete"))
                        {
                            if (existing.Collection is { } collection)
                                await PersonalLibrary.Current.DeleteCollectionAsync(collection, _lifetime.Token);
                            else await PersonalLibrary.Current.DeleteTagDefinitionAsync(existing.Tag!, _lifetime.Token);
                        }
                    }
                    else if (action == "convert" && existing?.Tag is { } tag)
                    {
                        if (ModalDialog.AskConfirmation(owner, $"Turn {tag.Name} into a Gallery folder? Tagged clips join it. No files move.", "Make folder", "Make folder"))
                            await PersonalLibrary.Current.ConvertTagToCollectionAsync(tag, _lifetime.Token);
                    }
                    else if (action == "edit" && existing != null)
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
                    else if (action == "create")
                    {
                        var name = new TextBox { MaxLength = ClipDetails.MaxTagLength };
                        AutomationProperties.SetName(name, "Label name");
                        var folder = new CheckBox { Content = "Show as a Gallery folder", Margin = new Thickness(0, 10, 0, 0) };
                        var color = new ComboBox { ItemsSource = TagPalette.Choices, SelectedIndex = 0 };
                        AutomationProperties.SetName(color, "Color");
                        var create = new StackPanel();
                        create.Children.Add(new TextBlock { Text = "Name" });
                        create.Children.Add(name);
                        create.Children.Add(new TextBlock { Text = "Color", Margin = new Thickness(0, 8, 0, 4) });
                        create.Children.Add(color);
                        create.Children.Add(folder);
                        var hint = new TextBlock { Text = "One folder per clip. No files move.", TextWrapping = TextWrapping.Wrap };
                        create.Children.Add(hint);
                        if (ModalDialog.Run(owner, "Create label", create, "Create", "Cancel", initialFocus: name,
                            validate: () => { if (ClipDetails.IsValidTag(name.Text.Trim())) return true; hint.Text = "Enter a name (1–32 characters)."; return false; }))
                        {
                            var hex = (color.SelectedItem as TagColorChoice)?.Hex;
                            if (folder.IsChecked == true)
                                await PersonalLibrary.Current.CreateCollectionAsync(new(Guid.NewGuid(), name.Text, selectedScope, hex, null), _lifetime.Token);
                            else await PersonalLibrary.Current.CreateTagDefinitionAsync(new(0, name.Text, selectedScope, hex), _lifetime.Token);
                        }
                    }
                    if (!_released) await ReloadAsync(rereadIndex: true);
                }
                catch (Exception ex) when (ex is LibraryConflictException or ArgumentException)
                {
                    if (!_released && owner.IsVisible) ModalDialog.Show(owner, ex.Message, "Couldn't update label");
                }
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
        bool accepted = ModalDialog.Run(owner, original == null ? "Create tag" : "Edit tag", panel, "Save", "Cancel", safeDefault: true,
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

    internal static LibraryCollection? EditCollection(Window owner, CollectionScope[] scopes, LibraryCollection? original, string? initialScope)
    {
        var name = new TextBox { Text = original?.Name ?? "", MaxLength = ClipDetails.MaxTagLength };
        var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == (original == null ? initialScope : original.GameId)) ?? scopes[0] };
        var color = new ComboBox { ItemsSource = TagPalette.Choices, SelectedItem = TagPalette.Choices.FirstOrDefault(c => c.Hex == original?.Color) ?? TagPalette.Choices[0] };
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
        panel.Children.Add(error);
        LibraryCollection? result = null;
        bool accepted = ModalDialog.Run(owner, original == null ? "Create folder" : "Edit folder", panel, "Save", "Cancel", safeDefault: true,
            validate: () =>
            {
                try
                {
                    result = new LibraryCollection(original?.Id ?? Guid.NewGuid(), name.Text,
                        (scope.SelectedItem as CollectionScope)?.Id, (color.SelectedItem as TagColorChoice)?.Hex,
                        original?.FolderLeaf).Normalize();
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
            AutomationProperties.SetName(list, "Move to folder");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Choose a folder for {targets.Length} clip(s). Files stay where they are.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) });
            panel.Children.Add(list);
            if (!ModalDialog.Run(owner, "Move to folder", panel, "Apply", "Cancel", safeDefault: true,
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
