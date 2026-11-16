using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Library;
using ClipBookmark = QuickClip.Gallery.ClipBookmark;
using PersonalLibrary = QuickClip.Library.PersonalLibrary;

namespace QuickClip.Views;

public partial class ClipDetailsEditor : UserControl
{
    private string? _path;
    private ClipDetails? _details;
    private ClipMediaStamp _fileStamp;
    private FileStamp? _sourceStamp;
    private double? _duration;
    private readonly List<string> _tags = [];
    private readonly Dictionary<double, Button> _bookmarkButtons = [];
    private DispatcherTimer? _autoSaveTimer;
    private bool _settingFields, _autoSaveEnabled;
    private long _draftRevision;
    private Task<bool>? _pendingSave;
    private bool _saveTimedOut;
    internal TimeSpan SaveTimeout { get; init; } = SaveDeadline.Duration;

    public ClipDetailsEditor()
    {
        InitializeComponent();
        _autoSaveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(850), DispatcherPriority.Background,
            async (_, _) =>
            {
                _autoSaveTimer!.Stop();
                using var deadline = new CancellationTokenSource(SaveTimeout);
                await WaitForSaveAsync(PersistDraftAsync(automatic: true, deadline.Token), deadline.Token);
            }, Dispatcher) { IsEnabled = false };
    }

    internal bool CloseButtonVisible { set => CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    internal bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set
        {
            _autoSaveEnabled = value;
            if (!value) _autoSaveTimer?.Stop();
            SaveButton.Content = value ? "Retry save" : "Save details";
            SaveButton.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
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
        ? new(_path, _fileStamp, _details) { Source = _sourceStamp } : null;
    private static DetailFields StoredFields(ClipDetails details) =>
        new(details.Title ?? "", details.Game, details.IsFavorite, details.Tags, "");

    internal DetailRecovery? CaptureRecovery() => HasDraft && _details is { } current
        ? new(StoredFields(current), new(TitleBox.Text, GameBox.Text, FavoriteBox.IsChecked == true, _tags.ToArray(), TagBox.Text))
        : null;

    internal bool RestoreRecovery(DetailRecovery recovery, bool apply)
    {
        if (_details is not { } current || HasDraft || IsSaving) return false;
        var saved = StoredFields(current);
        var before = recovery.Baseline;
        var draft = recovery.Draft;
        // Merge independently changed fields. If the same field changed on both sides,
        // keep the recovery file and leave the current committed details intact.
        static bool Conflict<T>(T saved, T before, T draft) =>
            !EqualityComparer<T>.Default.Equals(before, draft) &&
            !EqualityComparer<T>.Default.Equals(saved, before) &&
            !EqualityComparer<T>.Default.Equals(saved, draft);
        if (Conflict(CanonicalText(saved.Title), CanonicalText(before.Title), CanonicalText(draft.Title)) ||
            Conflict(CanonicalText(saved.Game), CanonicalText(before.Game), CanonicalText(draft.Game)) ||
            Conflict(saved.Favorite, before.Favorite, draft.Favorite) ||
            (!SameTags(before.Tags, draft.Tags) && !SameTags(saved.Tags, before.Tags) && !SameTags(saved.Tags, draft.Tags)))
            return false;
        if (!apply) return true;

        _settingFields = true;
        try
        {
            if (CanonicalText(before.Title) != CanonicalText(draft.Title)) TitleBox.Text = draft.Title;
            if (CanonicalText(before.Game) != CanonicalText(draft.Game)) GameBox.Text = draft.Game;
            if (before.Favorite != draft.Favorite) FavoriteBox.IsChecked = draft.Favorite;
            if (!SameTags(before.Tags, draft.Tags))
            {
                _tags.Clear();
                _tags.AddRange(draft.Tags);
            }
            TagBox.Text = draft.PendingTag;
            UpdateTagChips();
        }
        finally { _settingFields = false; }
        if (HasDraft) ShowSaveFailure("Recovered unsaved changes. Review them before retrying.");
        return true;
    }

    internal bool ApplyCommittedDetails(ClipDetails details)
    {
        if (_details is not { } current || ReferenceEquals(current, details) ||
            (current.IndexId != null && (current.IndexId != details.IndexId ||
                current.IndexRevision >= details.IndexRevision))) return false;
        if (HasDraft || IsSaving)
        {
            _autoSaveTimer?.Stop();
            ShowSaveFailure("Details changed in another view. Revert these fields to load the saved details.");
            return false;
        }

        _details = details;
        _settingFields = true;
        try
        {
            TitleBox.Text = details.Title ?? "";
            GameBox.Text = details.Game;
            FavoriteBox.IsChecked = details.IsFavorite;
            _tags.Clear();
            _tags.AddRange(details.Tags);
            UpdateTagChips();
            ShowBookmarks(details.Bookmarks);
        }
        finally { _settingFields = false; }
        return true;
    }

    internal void ShowSaveReminder() => Feedback.BringIntoView();

    internal bool ToggleFavorite()
    {
        bool favorite = FavoriteBox.IsChecked != true;
        FavoriteBox.IsChecked = favorite;
        return favorite;
    }

    internal async Task<bool> FlushAsync()
    {
        _autoSaveTimer?.Stop();
        using var deadline = new CancellationTokenSource(SaveTimeout);
        // At most the active snapshot and one follow-up. A change that arrives during
        // the follow-up stays dirty and the caller can keep the editor open.
        int writes = 0;
        if (_pendingSave is { IsCompleted: false } pending)
        {
            if (!await WaitForSaveAsync(pending, deadline.Token)) return false;
            writes++;
        }
        for (; writes < 2; writes++)
        {
            if (!AddTag()) return false;
            _autoSaveTimer?.Stop();
            if (!HasCommittedDraft) return true;
            if (!await WaitForSaveAsync(PersistDraftAsync(automatic: true, deadline.Token), deadline.Token))
                return false;
        }
        return !HasDraft;
    }

    private async Task<bool> WaitForSaveAsync(Task<bool> pending, CancellationToken token)
    {
        if (_saveTimedOut && !pending.IsCompleted) return false;
        bool saved = await SaveDeadline.WaitAsync(pending, token);
        if (!saved && token.IsCancellationRequested)
        {
            _saveTimedOut = !pending.IsCompleted;
            Log.WarnSlowSave();
            ShowSaveFailure("Storage is taking too long. Your changes are still here.");
        }
        return saved;
    }

    internal bool HasDraft => HasCommittedDraft || _details != null && !string.IsNullOrWhiteSpace(TagBox.Text);

    private static string CanonicalText(string? value)
    {
        string trimmed = (value ?? "").Trim();
        try { return trimmed.Normalize(); }
        catch (ArgumentException) { return trimmed; } // Keep malformed input dirty for the writer to validate.
    }

    private static bool SameTags(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Select(CanonicalText).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right.Select(CanonicalText));

    private bool HasCommittedDraft => _details is { } details &&
        (CanonicalText(TitleBox.Text) != CanonicalText(details.Title) ||
         CanonicalText(GameBox.Text) != CanonicalText(details.Game) ||
         FavoriteBox.IsChecked != details.IsFavorite || !SameTags(_tags, details.Tags));

    internal void Open(string path, ClipDetails details, bool focus = false, double? duration = null, FileStamp? source = null)
    {
        _autoSaveTimer?.Stop();
        _draftRevision++;
        _settingFields = true;
        try
        {
        _path = path;
        _details = details;
        _sourceStamp = source ?? ClipAnnotationTarget.ReadSource(path);
        _fileStamp = _sourceStamp is { } stamp
            ? new(stamp.Bytes, new DateTime(stamp.Modified, DateTimeKind.Utc), new DateTime(stamp.Created, DateTimeKind.Utc))
            : default;
        _duration = duration;
        CaptureDate.Text = $"Captured {details.CapturedAt.LocalDateTime:g}";
        string length = duration is { } seconds && double.IsFinite(seconds) && seconds >= 0 ? Fmt.Time(seconds) : "Unknown duration";
        MediaSummary.Text = $"{Path.GetFileName(path)} · {length}";
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
        if (AutoSaveEnabled) SaveButton.Visibility = RevertDetailsButton.Visibility = Visibility.Collapsed;
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
        _sourceStamp = null;
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
        if (AutoSaveEnabled) SaveButton.Visibility = RevertDetailsButton.Visibility = Visibility.Collapsed;
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
        if (HasCommittedDraft)
        {
            if (!IsSaving) Feedback.Text = "Changes pending";
            _autoSaveTimer?.Start();
        }
    }

    private Task<bool> PersistDraftAsync(bool automatic, CancellationToken token = default)
    {
        if (_pendingSave is { IsCompleted: false } pending) return pending;
        _saveTimedOut = false;
        return _pendingSave = PersistCoreAsync(automatic, token);
    }

    private async Task<bool> PersistCoreAsync(bool automatic, CancellationToken token)
    {
        _autoSaveTimer?.Stop();
        if (IsSaving || AnnotationTarget is not { } target || (automatic && !AutoSaveEnabled))
            return false;
        if (!automatic && !AddTag()) return false;
        if (!HasCommittedDraft)
        {
            // An explicit save can complete without a write when all fields are unchanged.
            if (!automatic) Saved?.Invoke(target.Path, target.Details);
            return true;
        }

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

        using var operation = Log.Begin(DiagnosticCategory.Editor, "Metadata save");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SaveTimeout);
        IsSaving = true;
        Feedback.Text = "Saving…";
        if (AutoSaveEnabled) SaveButton.Visibility = RevertDetailsButton.Visibility = Visibility.Collapsed;
        if (!automatic) IsEnabled = false;
        try
        {
            var result = await AnnotationActions.SaveDraftAsync(target, draft, timeout.Token);
            if (result.Details is not { } saved)
            {
                if (_path == target.Path && ReferenceEquals(_details, target.Details)) ShowSaveFailure(result.Error);
                return false;
            }
            operation?.Complete();
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
                    Feedback.Text = revision == _draftRevision ? "Saved" : "Changes pending";
                }
                else
                {
                    Open(target.Path, saved, duration: _duration, source: _sourceStamp);
                    Feedback.Text = "Details saved";
                }
                if (automatic) AutoSaved?.Invoke(target.Path, saved);
                else Saved?.Invoke(target.Path, saved);
            }
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            operation?.Cancel();
            if (ReferenceEquals(_details, target.Details)) ShowSaveFailure("Storage did not respond in time.");
            return false;
        }
        catch (Exception ex)
        {
            if (_path == target.Path && ReferenceEquals(_details, target.Details)) ShowSaveFailure();
            Log.Failure(DiagnosticCategory.Library, "Could not save clip details", ex);
            return false;
        }
        finally
        {
            IsSaving = false;
            if (!automatic) IsEnabled = true;
            // Re-save only when the user made NEW edits during this write; a failed
            // save must not create an endless retry loop.
            if (automatic && !timeout.IsCancellationRequested && revision != _draftRevision && HasDraft)
                ScheduleAutoSave();
        }
    }

    private void ShowSaveFailure(string? message = null)
    {
        Feedback.Text = message == null ? "Could not save details." : $"Could not save details. {message}";
        if (AutoSaveEnabled) SaveButton.Visibility = RevertDetailsButton.Visibility = Visibility.Visible;
    }

    private async void RevertDetails_Click(object sender, RoutedEventArgs e)
    {
        if (IsSaving || AnnotationTarget is not { } target) return;
        if (!ModalDialog.ConfirmAction(Window.GetWindow(this), "Discard the unsaved detail changes?",
            "Revert details", "Discard changes", "Keep editing", destructive: true)) return;
        _autoSaveTimer?.Stop();
        IsEnabled = false;
        try
        {
            var saved = await PersonalLibrary.Current.ReadAsync(target.Path);
            if (AnnotationTarget != target) return;
            if (saved == null && target.Source is { } stamp &&
                EditorSource.Check(target.Path, stamp, out _) == EditorSourceState.Ready)
                saved = ClipDetailsStore.Read(new FileInfo(target.Path), target.Details.Game, target.Details.CapturedAt);
            Open(target.Path, saved ?? target.Details, duration: _duration, source: target.Source);
            if (saved != null) AutoSaved?.Invoke(target.Path, saved);
        }
        finally { IsEnabled = true; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (AutoSaveEnabled) await FlushAsync();
        else await PersistDraftAsync(automatic: false);
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (AutoSaveEnabled)
        {
            if (!await FlushAsync()) return;
        }
        else if (_path != null && _details != null)
            Open(_path, _details, duration: _duration, source: _sourceStamp);
        Cancelled?.Invoke();
    }
}
