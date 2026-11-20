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
            string draftName = "";
            string? draftColor = null;
            bool draftFolder = false;
            while (!_released)
            {
                var definitions = await PersonalLibrary.Current.ReadCollectionsAsync(_lifetime.Token);
                var tags = await PersonalLibrary.Current.ReadTagDefinitionsAsync(_lifetime.Token);
                if (_released || !owner.IsVisible) return;
                var scopes = CollectionScopes(definitions, tags);
                var scope = new ComboBox { ItemsSource = scopes, SelectedItem = scopes.FirstOrDefault(s => s.Id == selectedScope) ?? scopes[0] };
                AutomationProperties.SetName(scope, "Available for");
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "Available for", Margin = new Thickness(0,0,0,5) });
                panel.Children.Add(scope);
                var rows = new StackPanel { Margin = new Thickness(0,12,0,8) };
                panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
                var dismiss = new Button();
                ManagedLabel? existing = null;
                string? action = null, newName = null, newColor = null;
                var createName = new TextBox { Text = draftName, MaxLength = ClipDetails.MaxTagLength, MinWidth = 100, VerticalContentAlignment = VerticalAlignment.Center };
                AutomationProperties.SetName(createName, "New label name");
                var createFolder = new CheckBox { Content = "Create as folder", IsChecked = draftFolder, Margin = new Thickness(0,8,0,0) };
                void Select(string next, ManagedLabel? label = null)
                {
                    draftName = createName.Text;
                    draftFolder = createFolder.IsChecked == true;
                    action = next; existing = label;
                    dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                Button IconButton(string glyph, string hint, Action click)
                {
                    var button = new Button { Content = glyph, ToolTip = hint, Width = 34, Height = 34, Padding = new Thickness(4), Margin = new Thickness(3,0,0,0), Focusable = true };
                    button.SetResourceReference(FrameworkElement.StyleProperty, "IconButton");
                    AutomationProperties.SetName(button, hint);
                    button.Click += (_, _) => click();
                    return button;
                }
                System.Windows.Media.Brush ColorBrush(string? hex) =>
                    (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex ?? "#9AA3B0")!;
                Button Swatch(string? hex, Action<string?> change, string label)
                {
                    var circle = new System.Windows.Shapes.Ellipse { Width = 19, Height = 19, Fill = ColorBrush(hex), StrokeThickness = 1 };
                    circle.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "BorderBrush");
                    var button = new Button { Content = circle, Width = 34, Height = 34, Padding = new Thickness(4), ToolTip = "Change color: " + label, Margin = new Thickness(0,0,6,0) };
                    AutomationProperties.SetName(button, "Change color: " + label);
                    var popup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
                    var palette = new WrapPanel { MaxWidth = 168 };
                    var frame = new Border { Child = palette, Padding = new Thickness(5), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
                    frame.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
                    frame.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                    popup.Child = frame;
                    foreach (var choice in TagPalette.Choices)
                    {
                        var dot = new System.Windows.Shapes.Ellipse { Width = 19, Height = 19, Fill = ColorBrush(choice.Hex), StrokeThickness = choice.Hex == hex ? 3 : 1 };
                        dot.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "PrimaryTextBrush");
                        var pick = new Button { Content = dot, Width = 34, Height = 34, Padding = new Thickness(4), ToolTip = choice.Label, Margin = new Thickness(1) };
                        AutomationProperties.SetName(pick, choice.Label);
                        pick.Click += (_, _) => { popup.IsOpen = false; circle.Fill = ColorBrush(choice.Hex); change(choice.Hex); };
                        palette.Children.Add(pick);
                    }
                    button.Click += (_, _) => popup.IsOpen = !popup.IsOpen;
                    button.Unloaded += (_, _) => popup.IsOpen = false;
                    return button;
                }
                void Populate()
                {
                    selectedScope = (scope.SelectedItem as CollectionScope)?.Id;
                    rows.Children.Clear();
                    var labels = definitions.Where(d => d.GameId == selectedScope).Select(d => new ManagedLabel(d, null, d.Name))
                        .Concat(tags.Where(t => t.GameId == selectedScope).Select(t => new ManagedLabel(null, t, t.Name)))
                        .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (labels.Length == 0) rows.Children.Add(new TextBlock { Text = "No labels yet.", Margin = new Thickness(0,8,0,8) });
                    foreach (var label in labels)
                    {
                        var row = new DockPanel { Margin = new Thickness(0,3,0,3), LastChildFill = true };
                        var swatch = Swatch(label.Collection?.Color ?? label.Tag?.Color, hex => { newColor = hex; Select("color", label); }, label.Label);
                        DockPanel.SetDock(swatch, Dock.Left); row.Children.Add(swatch);
                        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        DockPanel.SetDock(actions, Dock.Right);
                        var input = new TextBox { Text = label.Label, MaxLength = ClipDetails.MaxTagLength, Visibility = Visibility.Collapsed, VerticalContentAlignment = VerticalAlignment.Center };
                        AutomationProperties.SetName(input, "Rename " + label.Label);
                        var title = new TextBlock { Text = label.Label, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = label.Label };
                        bool editing = false;
                        Button? rename = null;
                        void SaveRename()
                        {
                            if (!ClipDetails.IsValidTag(input.Text.Trim())) { input.ToolTip = "Enter a valid label name"; return; }
                            newName = input.Text; Select("rename", label);
                        }
                        void CancelRename() { editing = false; input.Visibility = Visibility.Collapsed; title.Visibility = Visibility.Visible; rename!.Content = "\uE8AC"; input.Text = label.Label; }
                        rename = IconButton("\uE8AC", "Rename " + label.Label, () =>
                        {
                            if (editing) { SaveRename(); return; }
                            editing = true; title.Visibility = Visibility.Collapsed; input.Visibility = Visibility.Visible; rename!.Content = "\uE73E"; input.Focus(); input.SelectAll();
                        });
                        input.PreviewKeyDown += (_, args) =>
                        {
                            if (args.Key == System.Windows.Input.Key.Enter) { args.Handled = true; SaveRename(); }
                            else if (args.Key == System.Windows.Input.Key.Escape) { args.Handled = true; CancelRename(); }
                        };
                        actions.Children.Add(rename);
                        actions.Children.Add(IconButton("\uE74D", "Delete " + label.Label, () => Select("delete", label)));
                        var folder = IconButton("\uE838", label.Collection != null ? "Gallery folder" : "Make folder: related clips will appear in this Gallery folder", () => Select("convert", label));
                        folder.IsEnabled = label.Tag != null;
                        if (label.Collection != null) folder.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
                        actions.Children.Add(folder);
                        row.Children.Add(actions);
                        var body = new Grid { VerticalAlignment = VerticalAlignment.Center };
                        body.Children.Add(title); body.Children.Add(input); row.Children.Add(body);
                        rows.Children.Add(row);
                    }
                }
                scope.SelectionChanged += (_, _) => Populate();
                Populate();
                var add = new DockPanel { LastChildFill = true };
                var addColor = Swatch(draftColor, hex => draftColor = hex, "New label");
                DockPanel.SetDock(addColor, Dock.Left); add.Children.Add(addColor);
                var create = new Button { Content = "+ Add label", Margin = new Thickness(6,0,0,0), Padding = new Thickness(8,4,8,4) };
                create.IsEnabled = ClipDetails.IsValidTag(createName.Text.Trim());
                createName.TextChanged += (_, _) => create.IsEnabled = ClipDetails.IsValidTag(createName.Text.Trim());
                create.Click += (_, _) => { if (create.IsEnabled) Select("create"); };
                createName.PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Enter) { args.Handled = true; if (create.IsEnabled) Select("create"); } };
                DockPanel.SetDock(create, Dock.Right); add.Children.Add(create); add.Children.Add(createName);
                panel.Children.Add(add);
                panel.Children.Add(createFolder);
                panel.Children.Add(new TextBlock { Text = "Folders group matching clips in the Gallery. Video files stay in their current location.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,7,0,0) });
                ModalDialog.Run(owner, "Manage labels", panel, "Close", null, safeDefault: true, wide: true, dismissOnClick: dismiss, handleTextKeys: true);
                if (_released || !owner.IsVisible || action == null) break;
                try
                {
                    if (action == "delete" && existing != null)
                    {
                        if (ModalDialog.AskConfirmation(owner, $"Delete {existing.Label}? Your video files stay.", "Delete label", "Delete"))
                        {
                            if (existing.Collection is { } collection) await PersonalLibrary.Current.DeleteCollectionAsync(collection, _lifetime.Token);
                            else await PersonalLibrary.Current.DeleteTagDefinitionAsync(existing.Tag!, _lifetime.Token);
                        }
                    }
                    else if (action == "convert" && existing?.Tag is { } tag)
                    {
                        if (ModalDialog.AskConfirmation(owner, $"Move clips tagged {tag.Name} into a Gallery folder? Their video files stay where they are.", "Make folder", "Make folder"))
                            await PersonalLibrary.Current.ConvertTagToCollectionAsync(tag, _lifetime.Token);
                    }
                    else if ((action == "rename" || action == "color") && existing != null)
                    {
                        if (existing.Collection is { } collection)
                            await PersonalLibrary.Current.UpdateCollectionAsync(collection, collection with { Name = newName ?? collection.Name, Color = action == "color" ? newColor : collection.Color }, _lifetime.Token);
                        else if (existing.Tag is { } item)
                            await PersonalLibrary.Current.UpdateTagDefinitionAsync(item, item with { Name = newName ?? item.Name, Color = action == "color" ? newColor : item.Color }, _lifetime.Token);
                    }
                    else if (action == "create")
                    {
                        if (draftFolder) await PersonalLibrary.Current.CreateCollectionAsync(new(Guid.NewGuid(), draftName, selectedScope, draftColor, null), _lifetime.Token);
                        else await PersonalLibrary.Current.CreateTagDefinitionAsync(new(0, draftName, selectedScope, draftColor), _lifetime.Token);
                        draftName = ""; draftColor = null; draftFolder = false;
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
