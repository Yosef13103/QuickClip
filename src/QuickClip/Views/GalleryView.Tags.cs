using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    private sealed record TagChoice(string Name, int Count)
    {
        public string Label => $"{Name}  ·  {Count:N0}";
    }

    // The selection is captured after flushing pending metadata edits. The
    // dialog works only on that snapshot, never a later select-all/filter result.
    private async Task ManageSelectedTagsAsync()
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
                SelectionMode = SelectionMode.Multiple, MaxHeight = 160, MinHeight = 40 };
            var remove = new ListBox { ItemsSource = assigned, DisplayMemberPath = nameof(TagChoice.Label),
                SelectionMode = SelectionMode.Multiple, MaxHeight = 160, MinHeight = 40 };
            var input = new TextBox { MaxLength = ClipDetails.MaxTagLength };
            AutomationProperties.SetName(add, "Existing tags to add");
            AutomationProperties.SetName(remove, "Tags to remove");
            AutomationProperties.SetName(input, "New tag");
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"Edit tags on {targets.Length:N0} selected clip(s). Click multiple tags to select them. Unselected tags are unchanged.", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = "Add existing tags (Gallery counts)", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(add);
            panel.Children.Add(new TextBlock { Text = "Or add a new tag", Margin = new Thickness(0, 8, 0, 4) });
            panel.Children.Add(input);
            panel.Children.Add(new TextBlock { Text = "Remove tags (selected clip counts)", Margin = new Thickness(0, 12, 0, 4) });
            panel.Children.Add(remove);
            panel.Children.Add(error);
            string[] Additions() => add.SelectedItems.Cast<TagChoice>().Select(t => t.Name)
                .Concat(string.IsNullOrWhiteSpace(input.Text) ? Array.Empty<string>() : new[] { input.Text.Trim() })
                .DistinctBy(LibraryFiles.TextKey).ToArray();
            string[] Removals() => remove.SelectedItems.Cast<TagChoice>().Select(t => t.Name).ToArray();

            if (!ModalDialog.Run(owner, "Manage tags", panel, "Apply tags", "Cancel", safeDefault: true,
                validate: () =>
                {
                    var additions = Additions();
                    var removals = Removals();
                    if (additions.Length + removals.Length == 0)
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
                PersonalLibrary.Current.ApplyTagDeltaAsync(targets, Additions(), Removals(), _lifetime.Token));
            _annotationJob = job;
            int changed = await job;
            if (_released) return;

            // The commit succeeded. Refresh from the index, not a filesystem scan.
            var refresh = new List<ClipAnnotationResult>();
            foreach (var target in targets)
            {
                var details = await PersonalLibrary.Current.ReadAsync(target.Path, _lifetime.Token);
                if (details?.IndexId == target.Details.IndexId &&
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
