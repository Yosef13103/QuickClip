using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
    private DispatcherTimer? _autoSaveTimer;
    private bool _settingFields, _autoSaveEnabled;
    private long _draftRevision;

    public ClipDetailsEditor()
    {
        InitializeComponent();
        _autoSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(850), DispatcherPriority.Background,
            async (_, _) =>
            {
                _autoSaveTimer!.Stop();
                await PersistDraftAsync(automatic: true);
            }, Dispatcher) { IsEnabled = false };
    }

    /// <summary>Only Gallery opts in; Editor retains explicit save/cancel draft semantics.</summary>
    internal bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set
        {
            _autoSaveEnabled = value;
            if (!value) _autoSaveTimer?.Stop();
            SaveButton.Content = value ? "Save now" : "Save details";
            CancelButton.Content = value ? "Close" : "Cancel";
            AutoSaveHint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal event Action<string, ClipDetails>? AutoSaved;
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
        _autoSaveTimer?.Stop();
        _draftRevision++;
        _settingFields = true;
        try
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
        finally { _settingFields = false; }
    }

    /// <summary>Clears only this view's annotation state; never writes or removes files.</summary>
    internal void Clear()
    {
        _autoSaveTimer?.Stop();
        _draftRevision++;
        _settingFields = true;
        try
        {
        _path = null;
        _details = null;
        _fileStamp = default;
        _duration = null;
        CaptureDate.Text = "";
        MediaSummary.Text = "";
        LocationBox.Text = "";
        LocationBox.ToolTip = null;
        TitleBox.Clear();
        GameBox.Clear();
        FavoriteBox.IsChecked = false;
        TagBox.Clear();
        _tags.Clear();
        UpdateTagChips();
        ShowBookmarks([]);
        Feedback.Text = "";
        }
        finally { _settingFields = false; }
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
                ScheduleAutoSave();
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
        ScheduleAutoSave();
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

    private void UserField_Changed(object sender, RoutedEventArgs e) => ScheduleAutoSave();

    private void UserText_Changed(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

    private void ScheduleAutoSave()
    {
        if (!AutoSaveEnabled || _settingFields || _details == null || _path == null) return;
        _draftRevision++;
        _autoSaveTimer?.Stop();
        // Text typed into the tag input isn't yet a committed tag. Don't accidentally
        // persist half-entered values; Add tag / Enter explicitly commits it.
        if (HasDraft && string.IsNullOrWhiteSpace(TagBox.Text))
            _autoSaveTimer?.Start();
    }

    private async Task<bool> PersistDraftAsync(bool automatic)
    {
        _autoSaveTimer?.Stop();
        if (IsSaving || AnnotationTarget is not { } target || (automatic && !AutoSaveEnabled))
            return false;
        if (automatic && !string.IsNullOrWhiteSpace(TagBox.Text)) return false;
        if (!automatic && !AddTag()) return false;
        if (!HasDraft) return true;

        long revision = _draftRevision;
        string title = TitleBox.Text.Trim();
        string game = GameBox.Text.Trim();
        var draft = target.Details with
        {
            Title = title.Length == 0 ? null : title,
            Game = game.Length == 0 ? ClipLibrary.InitialGame(AppSettings.Current.ClipsFolder, target.Path) : game,
            Tags = _tags.ToArray(),
            IsFavorite = FavoriteBox.IsChecked == true,
        };

        IsSaving = true;
        if (!automatic) IsEnabled = false;
        try
        {
            var result = await AnnotationActions.SaveDraftAsync(target, draft, CancellationToken.None);
            if (result.Details is not { } saved)
            {
                if (_path == target.Path) Feedback.Text = result.Error ?? "Couldn't save clip details.";
                return false;
            }
            if (_path == target.Path && ReferenceEquals(_details, target.Details))
            {
                if (automatic)
                {
                    // Retain the user's current text and focus, even if they kept typing
                    // while the asynchronous metadata write was in flight.
                    _details = saved;
                    if (revision == _draftRevision && string.IsNullOrWhiteSpace(GameBox.Text) &&
                        !string.IsNullOrWhiteSpace(saved.Game))
                    {
                        // Empty classification resolves to its folder name. Show that
                        // canonical value so the same already-saved draft won't remain dirty.
                        _settingFields = true;
                        try { GameBox.Text = saved.Game; }
                        finally { _settingFields = false; }
                    }
                    Feedback.Text = "Saved automatically";
                }
                else
                {
                    Open(target.Path, saved, duration: _duration);
                    Feedback.Text = "Details saved";
                }
            }
            if (automatic) AutoSaved?.Invoke(target.Path, saved);
            else Saved?.Invoke(target.Path, saved);
            return true;
        }
        catch (Exception ex)
        {
            if (_path == target.Path) Feedback.Text = "Couldn't save clip details.";
            Log.Write("Could not save clip details: " + ex.Message);
            return false;
        }
        finally
        {
            IsSaving = false;
            if (!automatic) IsEnabled = true;
            // Re-save only when the user made NEW edits during this write; a failed
            // save must not create an endless retry loop.
            if (automatic && revision != _draftRevision && HasDraft)
                ScheduleAutoSave();
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) =>
        await PersistDraftAsync(automatic: false);

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (AutoSaveEnabled)
        {
            if (IsSaving)
            {
                Feedback.Text = "Saving your changes…";
                return;
            }
            if (!AddTag() || !await PersistDraftAsync(automatic: true)) return;
        }
        else if (_path != null && _details != null)
            Open(_path, _details, duration: _duration);
        Cancelled?.Invoke();
    }
}
