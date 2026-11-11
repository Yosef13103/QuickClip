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
    private (long Length, DateTime Modified, DateTime Created) _fileStamp;
    private readonly List<string> _tags = [];
    private readonly List<ClipBookmark> _bookmarks = [];
    private readonly Dictionary<double, TextBox> _bookmarkLabels = [];

    public ClipDetailsEditor() => InitializeComponent();
    internal event Action<string, ClipDetails>? Saved;
    internal event Action? Cancelled;
    internal event Action<IReadOnlyList<ClipBookmark>>? BookmarksChanged;
    internal Func<(double Position, double Duration)>? BookmarkPosition { get; set; }
    internal Action<double>? SeekBookmark { get; set; }
    internal void ShowSaveReminder()
    {
        Feedback.Text = "Save or cancel your clip details before exporting.";
        Feedback.BringIntoView();
    }
    internal bool HasDraft => _details is { } details &&
        (TitleBox.Text.Trim() != (details.Title ?? "") || GameBox.Text.Trim() != details.Game ||
         FavoriteBox.IsChecked != details.IsFavorite || !string.IsNullOrWhiteSpace(TagBox.Text) ||
         !_tags.SequenceEqual(details.Tags) || !_bookmarks.SequenceEqual(details.Bookmarks));

    internal void Open(string path, ClipDetails details, bool focus = false)
    {
        _path = path;
        _details = details;
        var file = new FileInfo(path);
        _fileStamp = file.Exists ? (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : default;
        CaptureDate.Text = $"Captured {details.CapturedAt.LocalDateTime:g}";
        TitleBox.Text = details.Title ?? "";
        GameBox.Text = details.Game;
        FavoriteBox.IsChecked = details.IsFavorite;
        TagBox.Clear();
        _tags.Clear();
        _tags.AddRange(details.Tags);
        UpdateTagChips();
        _bookmarks.Clear();
        _bookmarks.AddRange(details.Bookmarks);
        BookmarkPanel.Visibility = BookmarkPosition != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateBookmarkRows();
        Feedback.Text = "";
        if (focus) TitleBox.Focus();
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
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTag();
    }

    private void AddBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (BookmarkPosition?.Invoke() is { } time) AddBookmark(time.Position, time.Duration);
    }

    internal void AddBookmark(double position, double duration)
    {
        if (_details == null) return;
        if (!double.IsFinite(position) || !double.IsFinite(duration) || position < 0 || position >= duration)
        {
            Feedback.Text = "Move the playhead inside the clip to add a bookmark.";
            return;
        }
        double seconds = Math.Floor(position * 1000) / 1000;
        if (_bookmarkLabels.ContainsKey(seconds))
        {
            QueueBookmarkFocus(seconds);
            return;
        }
        if (_bookmarks.Count >= ClipDetails.MaxBookmarks)
        {
            Feedback.Text = $"This clip already has {ClipDetails.MaxBookmarks} bookmarks.";
            return;
        }
        _bookmarks.Add(new ClipBookmark(seconds, $"Bookmark at {Fmt.Time(seconds)}"));
        _bookmarks.Sort((left, right) => left.Seconds.CompareTo(right.Seconds));
        UpdateBookmarkRows();
        QueueBookmarkFocus(seconds);
        Feedback.Text = "";
    }

    internal void QueueBookmarkFocus(double seconds)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_bookmarkLabels.TryGetValue(seconds, out var box) || !box.IsVisible) return;
            box.BringIntoView();
            box.Focus();
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateBookmarkRows()
    {
        _bookmarkLabels.Clear();
        BookmarkRows.Children.Clear();
        if (BookmarkPosition == null) return;
        for (int i = 0; i < _bookmarks.Count; i++)
        {
            int index = i;
            var mark = _bookmarks[index];
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var seek = new Button { Content = mark.TimeText, ToolTip = $"Seek to {mark.TimeText}" };
            seek.Click += (_, _) => SeekBookmark?.Invoke(mark.Seconds);
            DockPanel.SetDock(seek, Dock.Left);
            row.Children.Add(seek);
            var remove = new Button { Content = "×", ToolTip = $"Remove bookmark at {mark.TimeText}" };
            AutomationProperties.SetName(remove, (string)remove.ToolTip);
            remove.Click += (_, _) =>
            {
                _bookmarks.RemoveAt(index);
                UpdateBookmarkRows();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var label = new TextBox { Text = mark.Label, MaxLength = ClipBookmark.MaxLabelLength };
            AutomationProperties.SetName(label, $"Bookmark label at {mark.TimeText}");
            label.TextChanged += (_, _) =>
            {
                _bookmarks[index] = mark with { Label = label.Text };
                BookmarksChanged?.Invoke(_bookmarks.ToArray());
            };
            row.Children.Add(label);
            BookmarkRows.Children.Add(row);
            _bookmarkLabels.Add(mark.Seconds, label);
        }
        BookmarksChanged?.Invoke(_bookmarks.ToArray());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || _details == null) return;
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) != _fileStamp)
            {
                Feedback.Text = "The file changed. Reopen it before saving details.";
                return;
            }
            string title = TitleBox.Text.Trim();
            string game = GameBox.Text.Trim();
            if (!AddTag()) return;
            if (_bookmarks.Any(mark => string.IsNullOrWhiteSpace(mark.Label) || mark.Label.Trim().Any(char.IsControl)))
            {
                Feedback.Text = "Give each bookmark a label before saving.";
                return;
            }
            var details = _details with
            {
                Title = title.Length == 0 ? null : title,
                Game = game.Length == 0 ? ClipLibrary.InitialGame(AppSettings.Current.ClipsFolder, _path) : game,
                Tags = _tags.ToArray(),
                IsFavorite = FavoriteBox.IsChecked == true,
                Bookmarks = _bookmarks.ToArray(),
            };
            details = details.Normalize();
            ClipDetailsStore.Write(_path, details);
            Open(_path, details);
            Feedback.Text = "Details saved";
            Saved?.Invoke(_path, details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Feedback.Text = "Couldn’t save clip details.";
            Log.Write("Could not save clip details: " + ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null && _details != null) Open(_path, _details);
        Cancelled?.Invoke();
    }
}
