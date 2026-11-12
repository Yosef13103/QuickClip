using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QuickClip.Gallery;

namespace QuickClip.Views;

public partial class ClipDetailsEditor : UserControl
{
    private string? _path;
    private ClipDetails? _details;
    private ClipMediaStamp _fileStamp;
    private double? _duration;
    private readonly List<string> _tags = [];
    private readonly Dictionary<double, Button> _bookmarkButtons = [];

    public ClipDetailsEditor() => InitializeComponent();
    internal event Action<string, ClipDetails>? Saved;
    internal event Action? Cancelled;
    internal event Action<IReadOnlyList<ClipBookmark>>? BookmarksChanged;
    internal Action<double>? SeekBookmark { get; set; }
    internal Func<IReadOnlyList<string>>? KnownTags { get; set; }
    internal IClipAnnotationActions AnnotationActions { get; set; } = LegacyClipAnnotationActions.Instance;
    internal bool IsSaving { get; private set; }
    internal ClipAnnotationTarget? AnnotationTarget => _path != null && _details != null
        ? new(_path, _fileStamp, _details) : null;
    internal void ShowSaveReminder(string message = "Save or cancel your clip details before exporting.")
    {
        Feedback.Text = message;
        Feedback.BringIntoView();
    }
    internal bool HasDraft => _details is { } details &&
        (TitleBox.Text.Trim() != (details.Title ?? "") || GameBox.Text.Trim() != details.Game ||
         FavoriteBox.IsChecked != details.IsFavorite || !string.IsNullOrWhiteSpace(TagBox.Text) ||
         !_tags.SequenceEqual(details.Tags));

    internal void Open(string path, ClipDetails details, bool focus = false, double? duration = null)
    {
        _path = path;
        _details = details;
        var file = new FileInfo(path);
        _fileStamp = file.Exists ? ClipMediaStamp.Read(file) : default;
        _duration = duration;
        CaptureDate.Text = $"Captured {details.CapturedAt.LocalDateTime:g}";
        string length = duration is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? Fmt.Time(seconds) : "Unknown duration";
        MediaSummary.Text = $"{file.Name} · {length}";
        LocationBox.Text = path;
        LocationBox.ToolTip = path;
        TitleBox.Text = details.Title ?? "";
        GameBox.Text = details.Game;
        FavoriteBox.IsChecked = details.IsFavorite;
        TagBox.Clear();
        _tags.Clear();
        _tags.AddRange(details.Tags);
        UpdateTagChips();
        ShowBookmarks(details.Bookmarks);
        Feedback.Text = "";
        if (focus) TitleBox.Focus();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || !File.Exists(_path))
        {
            Feedback.Text = "The media file is unavailable.";
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + _path } }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Feedback.Text = "Couldn’t reveal this clip in Explorer.";
        }
    }

    private void UpdateTagChips()
    {
        TagChips.Children.Clear();
        foreach (string tag in _tags)
        {
            var remove = new Button
            {
                Content = tag + "  ×",
                ToolTip = $"Remove tag {tag}",
                Margin = new Thickness(0, 3, 4, 0),
                Padding = new Thickness(6, 2, 6, 2),
            };
            remove.Click += (_, _) =>
            {
                _tags.Remove(tag);
                UpdateTagChips();
                TagBox.Focus();
            };
            TagChips.Children.Add(remove);
        }
        UpdateTagSuggestions();
    }

    private void TagBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized) UpdateTagSuggestions();
    }

    private void UpdateTagSuggestions()
    {
        TagSuggestions.Children.Clear();
        foreach (string tag in ClipTagChoices.Match(KnownTags?.Invoke() ?? [], TagBox.Text, _tags).Take(6))
        {
            var button = new Button { Content = tag, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 2) };
            AutomationProperties.SetName(button, $"Add existing tag {tag}");
            button.Click += (_, _) =>
            {
                TagBox.Text = tag;
                AddTag();
                TagBox.Focus();
            };
            TagSuggestions.Children.Add(button);
        }
    }

    private bool AddTag()
    {
        string tag = TagBox.Text.Trim();
        if (tag.Length == 0) return true;
        if (!ClipDetails.IsValidTag(tag) ||
            (_tags.Count >= ClipDetails.MaxTags && !_tags.Contains(tag, StringComparer.OrdinalIgnoreCase)))
        {
            Feedback.Text = $"Use up to {ClipDetails.MaxTags} tags of {ClipDetails.MaxTagLength} characters each.";
            return false;
        }
        if (!_tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) _tags.Add(tag);
        TagBox.Clear();
        Feedback.Text = "";
        UpdateTagChips();
        return true;
    }

    private void AddTag_Click(object sender, RoutedEventArgs e)
    {
        AddTag();
        TagBox.Focus();
    }

    private void TagBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && TagSuggestions.Children.OfType<Button>().FirstOrDefault() is { } suggestion)
        {
            suggestion.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTag();
    }

    internal void QueueBookmarkFocus(double seconds)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_bookmarkButtons.TryGetValue(seconds, out var button) || !button.IsVisible) return;
            button.BringIntoView();
            button.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ShowBookmarks(IReadOnlyList<ClipBookmark> bookmarks)
    {
        BookmarkCount.Text = bookmarks.Count == 1 ? "1 bookmark" : $"{bookmarks.Count} bookmarks";
        BookmarkCount.Visibility = bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        BookmarkPanel.Visibility = SeekBookmark != null && bookmarks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _bookmarkButtons.Clear();
        BookmarkRows.Children.Clear();
        if (SeekBookmark == null) return;
        foreach (var mark in bookmarks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var seek = new Button { Content = mark.TimeText, ToolTip = $"Seek to {mark.TimeText}" };
            AutomationProperties.SetName(seek, $"Seek to {mark.Label} at {mark.TimeText}");
            seek.Click += (_, _) => SeekBookmark?.Invoke(mark.Seconds);
            DockPanel.SetDock(seek, Dock.Left);
            row.Children.Add(seek);
            row.Children.Add(new TextBlock
            {
                Text = mark.Label,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
            BookmarkRows.Children.Add(row);
            _bookmarkButtons[mark.Seconds] = seek;
        }
        BookmarksChanged?.Invoke(bookmarks);
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (IsSaving || AnnotationTarget is not { } target) return;
        try
        {
            string title = TitleBox.Text.Trim();
            string game = GameBox.Text.Trim();
            if (!AddTag()) return;
            var details = target.Details with
            {
                Title = title.Length == 0 ? null : title,
                Game = game.Length == 0 ? ClipLibrary.InitialGame(AppSettings.Current.ClipsFolder, target.Path) : game,
                Tags = _tags.ToArray(),
                IsFavorite = FavoriteBox.IsChecked == true,
            };
            IsSaving = true;
            IsEnabled = false;
            var result = await AnnotationActions.SaveDraftAsync(target, details, CancellationToken.None);
            if (result.Details is not { } saved)
            {
                Feedback.Text = result.Error;
                return;
            }
            if (_path == target.Path && ReferenceEquals(_details, target.Details))
            {
                Open(target.Path, saved, duration: _duration);
                Feedback.Text = "Details saved";
            }
            Saved?.Invoke(target.Path, saved);
        }
        catch (Exception ex)
        {
            Feedback.Text = "Couldn’t save clip details.";
            Log.Write("Could not save clip details: " + ex.Message);
        }
        finally
        {
            IsSaving = false;
            IsEnabled = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null && _details != null) Open(_path, _details, duration: _duration);
        Cancelled?.Invoke();
    }
}
