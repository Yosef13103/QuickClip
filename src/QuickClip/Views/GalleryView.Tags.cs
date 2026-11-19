using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    private string[] _exactTags = [];
    private bool _matchAllTags = true;

    private void RefreshTagFilterButton()
    {
        if (MultiTagFilterButton == null) return;
        MultiTagFilterButton.Content = _exactTags.Length == 0
            ? "Tags ▾" : $"Tags ({_exactTags.Length} · {(_matchAllTags ? "all" : "any")}) ▾";
        MultiTagFilterButton.ToolTip = _exactTags.Length == 0
            ? "Filter by one or more tags" : string.Join(", ", _exactTags);
        ActiveTagFilters.ItemsSource = _exactTags;
        ActiveTagFilters.Visibility = _exactTags.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ToggleTagFilter(string tag)
    {
        string key = LibraryFiles.TextKey(tag);
        _exactTags = _exactTags.Any(t => LibraryFiles.TextKey(t) == key)
            ? _exactTags.Where(t => LibraryFiles.TextKey(t) != key).ToArray()
            : [.. _exactTags, tag];
        RefreshTagFilterButton();
        OrganizationFilter_Changed(this, new RoutedEventArgs());
    }

    private void TagChip_Click(object sender, RoutedEventArgs e)
    {
        if (_released || !PersonalAnnotations || sender is not Button { DataContext: ClipItem.TagBadge badge }) return;
        e.Handled = true;
        ToggleTagFilter(badge.Name);
    }

    private void ActiveTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string tag }) ToggleTagFilter(tag);
    }

    private void MultiTagFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_released || !PersonalAnnotations) return;
        // One nonmodal, searchable picker; selecting a tag never opens a clip.
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = MultiTagFilterButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false, AllowsTransparency = true,
        };
        var panel = new StackPanel { Width = 260 };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 8), ToolTip = "Search tags" };
        AutomationProperties.SetName(search, "Search available tags");
        var matchAll = new CheckBox { Content = "Match all selected tags", IsChecked = _matchAllTags, Margin = new Thickness(0, 0, 0, 6) };
        var list = new StackPanel();
        var scroll = new ScrollViewer { Content = list, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var clear = new Button { Content = "Clear tags", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        void Populate()
        {
            list.Children.Clear();
            foreach (string name in KnownTags.Concat(_exactTags).DistinctBy(LibraryFiles.TextKey)
                .Where(tag => tag.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase))
            {
                var check = new CheckBox { Content = name, IsChecked = _exactTags.Any(tag => LibraryFiles.TextKey(tag) == LibraryFiles.TextKey(name)), Margin = new Thickness(0, 3, 0, 3) };
                check.Click += (_, _) => ToggleTagFilter(name);
                list.Children.Add(check);
            }
        }
        search.TextChanged += (_, _) => Populate();
        matchAll.Click += (_, _) =>
        {
            _matchAllTags = matchAll.IsChecked == true;
            RefreshTagFilterButton();
            OrganizationFilter_Changed(this, new RoutedEventArgs());
        };
        clear.Click += (_, _) =>
        {
            _exactTags = [];
            RefreshTagFilterButton();
            Populate();
            OrganizationFilter_Changed(this, new RoutedEventArgs());
        };
        panel.Children.Add(search);
        panel.Children.Add(matchAll);
        panel.Children.Add(scroll);
        panel.Children.Add(clear);
        var border = new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Child = panel };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "AccentFillColorDefaultBrush");
        popup.Child = border;
        popup.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == System.Windows.Input.Key.Escape)
            {
                popup.IsOpen = false;
                MultiTagFilterButton.Focus();
                args.Handled = true;
            }
        };
        Populate();
        popup.IsOpen = true;
        search.Focus();
    }

    private async void LibraryTags_Click(object sender, RoutedEventArgs e)
    {
        if (!PersonalLibrary.Enabled || _loading || _released || _editingAnnotations ||
            Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner ||
            !await FlushAnnotationsAsync() || _released || _loading || _editingAnnotations) return;
        _editingAnnotations = true;
        GalleryDetailsEditor.IsEnabled = false;
        try
        {
            var old = new ComboBox { ItemsSource = KnownTags.ToArray(), SelectedIndex = 0 };
            var replacement = new TextBox { MaxLength = ClipDetails.MaxTagLength };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetName(old, "Library tag to rename or merge");
            AutomationProperties.SetName(replacement, "Replacement tag");
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Rename or merge a tag across available indexed clips in configured folders, regardless of current Gallery filters. Missing, unavailable and excluded clips are unchanged. Maximum 256 matching clips per operation.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Existing tag", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(old);
            panel.Children.Add(new TextBlock { Text = "Replace with (existing or new)", Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(replacement);
            panel.Children.Add(error);
            if (!ModalDialog.Run(owner, "Library tags", panel, "Review changes", "Cancel", safeDefault: true,
                validate: () =>
                {
                    var name = replacement.Text.Trim();
                    if (old.SelectedItem is string source && ClipDetails.IsValidTag(name) &&
                        LibraryFiles.TextKey(source) != LibraryFiles.TextKey(name)) return true;
                    error.Text = "Choose an existing tag and a different valid replacement.";
                    return false;
                })) return;
            if (old.SelectedItem is not string sourceTag || _released) return;
            string newTag = replacement.Text.Trim();
            var targets = await PersonalLibrary.Current.ReadTagTargetsAsync(sourceTag, _lifetime.Token);
            if (_released) return;
            if (targets.Length == 0) { ShowStatus("No available indexed clips use this tag."); return; }
            if (!ModalDialog.AskConfirmation(owner,
                $"Replace {sourceTag} with {newTag} on {targets.Length:N0} indexed clips? Only the reviewed clips will change. Other annotations and media are untouched.",
                "Confirm library tag replacement", "Replace tags")) return;
            if (_released || !PersonalLibrary.Enabled) return;
            var job = WithProgressAsync("Replacing library tag", targets.Length, _ =>
                PersonalLibrary.Current.ApplyTagDeltaAsync(targets, [newTag], [sourceTag], _lifetime.Token, replaceOnlyPresent: true));
            _annotationJob = job;
            int changed = await job;
            if (_released) return;
            await ReloadAsync(rereadIndex: true);
            ShowStatus($"Replaced tags on {changed:N0} clip(s).");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Library tag replacement failed", ex);
            if (!_released) ShowStatus(ex is LibraryConflictException or ArgumentException ? ex.Message :
                "Could not complete library tag replacement. Refresh before retrying.");
        }
        finally
        {
            _annotationJob = null;
            _editingAnnotations = false;
            GalleryDetailsEditor.IsEnabled = true;
        }
    }

    private async void TagColors_Click(object sender, RoutedEventArgs e)
    {
        if (!PersonalLibrary.Enabled || _released || _loading || _editingAnnotations ||
            Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner ||
            !await FlushAnnotationsAsync() || _released || _loading || _editingAnnotations) return;
        _editingAnnotations = true;
        try
        {
            var tags = new ComboBox { ItemsSource = KnownTags.ToArray(), SelectedIndex = 0 };
            var colors = new ComboBox { ItemsSource = TagPalette.Choices, SelectedIndex = 0 };
            AutomationProperties.SetName(tags, "Tag to color");
            AutomationProperties.SetName(colors, "Tag color");
            void SelectColor()
            {
                if (tags.SelectedItem is not string name) return;
                string key = LibraryFiles.TextKey(name);
                var color = _clips.Select(clip => clip.Details.TagColors.GetValueOrDefault(key))
                    .FirstOrDefault(value => value != null);
                colors.SelectedItem = TagPalette.Choices.FirstOrDefault(choice => choice.Hex == color) ?? TagPalette.Choices[0];
            }
            tags.SelectionChanged += (_, _) => SelectColor();
            SelectColor();
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Choose a color for this tag throughout your library. Default removes the color. Tag names remain visible regardless of color.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Tag", Margin = new Thickness(0, 10, 0, 4) });
            panel.Children.Add(tags);
            panel.Children.Add(new TextBlock { Text = "Color", Margin = new Thickness(0, 10, 0, 4) });
            panel.Children.Add(colors);
            if (!ModalDialog.Run(owner, "Tag colors", panel, "Apply color", "Cancel", safeDefault: true,
                validate: () => tags.SelectedItem is string && colors.SelectedItem is TagColorChoice)) return;
            if (_released || !PersonalLibrary.Enabled || tags.SelectedItem is not string tag ||
                colors.SelectedItem is not TagColorChoice choice) return;
            var job = PersonalLibrary.Current.SetTagColorAsync(tag, choice.Hex, _lifetime.Token);
            _annotationJob = job;
            await job;
            if (!_released) await ReloadAsync(rereadIndex: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Tag color update failed", ex);
            if (!_released) ShowStatus("Could not change tag color. Refresh before retrying.");
        }
        finally { _annotationJob = null; _editingAnnotations = false; }
    }

    private sealed record TagChoice(string Name, int Count)
    {
        public string Label => $"{Name}  ·  {Count:N0}";
    }

    // The selection is captured after flushing pending metadata edits. The
    // dialog works only on that snapshot, never a later select-all/filter result.
    private async Task ManageSelectedTagsAsync(bool replace = false)
    {
        if (!PersonalLibrary.Enabled || _editingAnnotations || _released || _loading ||
            !SelectionActions.IsEnabled || Window.GetWindow(this) is not { IsVisible: true, Content: Panel } owner)
            return;
        if (!await FlushAnnotationsAsync() || _released || _editingAnnotations || _loading) return;
        var clips = CurrentClips.Where(clip => clip.IsSelected).ToArray();
        if (clips.Length == 0) return;
        if (clips.Length > 256 || clips.Any(clip => clip.Details.IndexId == null || !IsAvailable(clip)))
        {
            ShowStatus("Select up to 256 available, indexed clips. Refresh the Gallery if needed.");
            return;
        }

        _hoverPreview.Stop();
        _editingAnnotations = true;
        GalleryDetailsEditor.IsEnabled = false;
        try
        {
            var targets = clips.Select(ClipAnnotationTarget.For).ToArray();
            if (targets.Any(target => target.Source?.Identity == null))
                throw new LibraryConflictException("A selected file is unavailable. Refresh and try again.");

            static TagChoice[] Choices(IEnumerable<ClipDetails> details) => details
                .SelectMany(d => d.Tags.DistinctBy(LibraryFiles.TextKey))
                .GroupBy(LibraryFiles.TextKey)
                .Select(group => new TagChoice(group.First(), group.Count()))
                .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToArray();

            var available = Choices(_clips.Select(clip => clip.Details));
            var assigned = Choices(targets.Select(target => target.Details));
            var add = new ListBox { ItemsSource = available, DisplayMemberPath = nameof(TagChoice.Label),
                SelectionMode = replace ? SelectionMode.Single : SelectionMode.Multiple, MaxHeight = 160, MinHeight = 40 };
            var remove = new ListBox { ItemsSource = assigned, DisplayMemberPath = nameof(TagChoice.Label),
                SelectionMode = replace ? SelectionMode.Single : SelectionMode.Multiple, MaxHeight = 160, MinHeight = 40 };
            var input = new TextBox { MaxLength = ClipDetails.MaxTagLength };
            AutomationProperties.SetName(add, "Existing tags to add");
            AutomationProperties.SetName(remove, "Tags to remove");
            AutomationProperties.SetName(input, "New tag");
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = replace ? $"Replace one tag on {targets.Length:N0} selected clip(s). Only clips carrying the old tag are changed." : $"Edit tags on {targets.Length:N0} selected clip(s). Click multiple tags to select them. Unselected tags are unchanged.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Add existing tags (Gallery counts)", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(add);
            panel.Children.Add(new TextBlock { Text = "Or add a new tag", Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = replace ? "Old tag (selected clip counts)" : "Remove tags (selected clip counts)", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(remove);
            panel.Children.Add(error);
            string[] Additions() => add.SelectedItems.Cast<TagChoice>().Select(t => t.Name)
                .Concat(string.IsNullOrWhiteSpace(input.Text) ? Array.Empty<string>() : new[] { input.Text.Trim() })
                .DistinctBy(LibraryFiles.TextKey).ToArray();
            string[] Removals() => remove.SelectedItems.Cast<TagChoice>().Select(t => t.Name).ToArray();

            if (!ModalDialog.Run(owner, replace ? "Replace tag" : "Manage tags", panel, replace ? "Replace tag" : "Apply tags", "Cancel", safeDefault: true,
                validate: () =>
                {
                    var additions = Additions();
                    var removals = Removals();
                    if (replace && (additions.Length != 1 || removals.Length != 1))
                        error.Text = "Choose one old tag and one replacement tag.";
                    else if (additions.Length + removals.Length == 0)
                        error.Text = "Choose at least one change.";
                    else if (additions.Length > ClipDetails.MaxTags || removals.Length > ClipDetails.MaxTags ||
                        additions.Any(t => !ClipDetails.IsValidTag(t)))
                        error.Text = "Too many tags, or an invalid tag.";
                    else if (additions.Select(LibraryFiles.TextKey).Intersect(removals.Select(LibraryFiles.TextKey)).Any())
                        error.Text = "A tag cannot be added and removed at the same time.";
                    else { error.Text = ""; return true; }
                    return false;
                })) return;
            if (_released || !PersonalLibrary.Enabled) return;

            var job = WithProgressAsync("Updating tags", targets.Length, _ =>
                PersonalLibrary.Current.ApplyTagDeltaAsync(targets, Additions(), Removals(), _lifetime.Token, replaceOnlyPresent: replace));
            _annotationJob = job;
            int changed = await job;
            if (_released) return;

            // The commit succeeded. Refresh from the index, not a filesystem scan.
            var refresh = new List<ClipAnnotationResult>();
            foreach (var target in targets)
            {
                var details = await PersonalLibrary.Current.ReadAsync(target.Path, _lifetime.Token);
                if (details is not null && details.IndexId == target.Details.IndexId &&
                    _clips.Any(clip => SavedRecordingFolders.Paths.Equals(clip.Path, target.Path) &&
                        clip.Details.IndexId == details.IndexId))
                    refresh.Add(new(target.Path, details, null));
            }
            if (_released) return;
            ApplyClipDetails(refresh);
            if (refresh.Count != targets.Length) await ReloadAsync(rereadIndex: true);
            ShowStatus(changed == 0 ? "Tags already match." : $"Updated tags on {changed:N0} clip(s).");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.Library, "Gallery tag management failed", ex);
            if (!_released) ShowStatus(ex is LibraryConflictException or ArgumentException ? ex.Message :
                "Could not complete tag management. Refresh before retrying.");
        }
        finally
        {
            _annotationJob = null;
            _editingAnnotations = false;
            GalleryDetailsEditor.IsEnabled = true;
        }
    }
}
