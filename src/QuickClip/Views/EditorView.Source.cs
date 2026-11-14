using System.Windows;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Views;

public partial class EditorView
{
    private FileStamp? _sourceStamp;
    private EditorSourceState _sourceState;
    private bool _sourceSuspended, _recheckingSource, _replacingSource;
    private bool _previewReady;
    private bool _restoreRepaintQueued;
    private Window? _sourceWindow;
    private bool SourceReady => _sourceState == EditorSourceState.Ready && !_sourceSuspended;

    private void AttachSourceEvents()
    {
        ClipDeletion.Current.Releasing += OnSourceReleasing;
        ClipDeletion.Current.Changed += OnSourceDeletionChanged;
        _sourceWindow = Window.GetWindow(this);
        if (_sourceWindow != null)
        {
            _sourceWindow.Activated += OnSourceWindowActivated;
            _sourceWindow.StateChanged += OnSourceWindowStateChanged;
            _sourceWindow.IsVisibleChanged += OnSourceWindowVisibilityChanged;
        }
    }

    private void DetachSourceEvents()
    {
        ClipDeletion.Current.Releasing -= OnSourceReleasing;
        ClipDeletion.Current.Changed -= OnSourceDeletionChanged;
        if (_sourceWindow != null)
        {
            _sourceWindow.Activated -= OnSourceWindowActivated;
            _sourceWindow.StateChanged -= OnSourceWindowStateChanged;
            _sourceWindow.IsVisibleChanged -= OnSourceWindowVisibilityChanged;
        }
    }

    private void OnSourceReleasing(IReadOnlyList<string> paths)
    {
        if (_closed || _deletingMedia || _replacingSource || CurrentFile is not { } path ||
            !EditorSource.Matches(path, paths)) return;
        _sourceSuspended = true;
        StopSourcePreview();
        UpdateUiState();
    }

    private void OnSourceDeletionChanged() => Dispatcher.BeginInvoke(() => _ = RefreshSourceAsync());
    private async void OnSourceWindowActivated(object? sender, EventArgs e)
    {
        if (IsVisible) await RefreshSourceAsync();
        QueueNativePreviewRepaint();
    }

    private void OnSourceWindowStateChanged(object? sender, EventArgs e)
    {
        if (_sourceWindow?.WindowState != WindowState.Minimized)
            QueueNativePreviewRepaint();
    }

    private void OnSourceWindowVisibilityChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) QueueNativePreviewRepaint();
    }

    /// <summary>
    /// WPF restores the chrome before the HwndHost child is positioned/composited.
    /// Coalesce tray, taskbar and activation events and repaint only after layout has settled.
    /// Never unload/reopen media or change the paused state or playhead.
    /// </summary>
    private void QueueNativePreviewRepaint()
    {
        if (_restoreRepaintQueued || _closed || _sourceWindow is not { IsVisible: true } window ||
            window.WindowState == WindowState.Minimized) return;
        _restoreRepaintQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _restoreRepaintQueued = false;
                if (EditorSource.ShouldRepaintNativePreview(
                    _sourceWindow?.IsVisible == true,
                    _sourceWindow?.WindowState == WindowState.Minimized,
                    IsVisible, SourceReady, _previewReady, _media != null))
                    Video.RefreshAfterRestore();
            }), DispatcherPriority.Background)), DispatcherPriority.Loaded);
    }

    private void StopSourcePreview()
    {
        _waveCts?.Cancel();
        _keyframeCts?.Cancel();
        _soundCts?.Cancel();
        _soundPreviewTimer.Stop();
        _audioSyncTimer.Stop();
        _paused = true;
        _previewReady = false;
        // libmpv can retain the last rendered frame while idle. Hide its HWND until
        // a FileLoaded event verifies that the requested current clip is playing.
        Video.Visibility = Visibility.Hidden;
        _player?.Command("stop");
    }

    private void ClearNativePreview()
    {
        StopSourcePreview();
        _player?.Command("playlist-clear");
        _player?.SetList("audio-files", []);
        EmptySourceHint.Text = "Open a video or drop it here";
        EmptySourceHint.Visibility = Visibility.Visible;
    }

    private void SetSourceState(EditorSourceState state)
    {
        _sourceState = state;
        _sourceSuspended = false;
        bool ready = state == EditorSourceState.Ready;
        if (!ready)
        {
            StopSourcePreview();
            _exportCts?.Cancel();
            UnavailableSourceText.Text = EditorSource.Message(state) +
                "\n\nYour edits are kept in this editor. Restore the original source to export them.";
        }
        bool showBlockingPrompt = AppProfile.IsPersonal && !ready && _media != null;
        MissingSourceOverlay.Visibility = showBlockingPrompt ? Visibility.Visible : Visibility.Collapsed;
        SetRecoveryInteraction(showBlockingPrompt);
        if (showBlockingPrompt)
        {
            if (IsVisible && !MissingSourceOverlay.IsKeyboardFocusWithin) RetryMissingSourceButton.Focus();
            MissingSourceOverlayTitle.Text = state == EditorSourceState.Missing ? "Recording is missing" : "Recording unavailable";
            MissingSourceOverlayMessage.Text = EditorSource.Message(state) +
                "\n\nClear editor closes this clip without deleting any recording.";
        }
        Video.Visibility = ready && _media != null && _previewReady
            ? Visibility.Visible : Visibility.Hidden;
        EmptySourceHint.Visibility = ready
            ? _previewReady && _media != null ? Visibility.Collapsed : Visibility.Visible
            : Visibility.Collapsed;
        if (ready && _media == null)
            EmptySourceHint.Text = "Open a video or drop it here";
        UnavailableSource.Visibility = ready || showBlockingPrompt ? Visibility.Collapsed : Visibility.Visible;
        Timeline.HasMedia = ready && _media != null;
        UpdateUiState();
        if (AppProfile.IsPersonal) ClearMissingSourceButton.IsEnabled = _media != null && _exportCts == null;
        UpdateViewSeek();
    }

    private void SetRecoveryInteraction(bool blocked)
    {
        EditorHeader.IsEnabled = TimelineDivider.IsEnabled = InspectorDivider.IsEnabled = !blocked;
        AllowDrop = !blocked;
        if (blocked) SoundDropHint.Visibility = Visibility.Collapsed;
    }

    private bool VerifySource(string path)
    {
        var state = EditorSource.Check(path, _sourceStamp, out _);
        if (state == EditorSourceState.Ready) return true;
        SetSourceState(state);
        return false;
    }

    private async void RetrySource_Click(object sender, RoutedEventArgs e) => await RefreshSourceAsync();

    private void ClearUnavailableSource_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal || SourceReady || _media == null || _closed || _loading ||
            _deletingMedia || _exportCts != null || _recheckingSource || MetadataEditor.IsSaving)
            return;
        if (!ConfirmDiscardDraft()) return;

        // Forget the unavailable source in this editor only. Never delete media or Gallery entries.
        ClearNativePreview();
        _keyframeCts?.Cancel();
        _stalePreviews.AddRange(_tracks.Select(t => t.PreviewFile).OfType<string>());
        foreach (var track in _tracks)
        {
            track.Changed -= OnTrackChanged;
            track.Moved -= OnSoundMoved;
            track.RemoveRequested -= RemoveSound;
        }
        _tracks.Clear();
        DeleteStalePreviews();
        MetadataEditor.Clear();
        _media = null;
        _sourceStamp = null;
        _openedDraft = null;
        _undo = null;
        _lastExport = null;
        _sourceState = EditorSourceState.Ready;
        _sourceSuspended = false;

        MissingSourceOverlay.Visibility = UnavailableSource.Visibility = Visibility.Collapsed;
        SetRecoveryInteraction(false);
        Video.Visibility = Visibility.Hidden;
        EmptySourceHint.Visibility = Visibility.Visible;
        RevealButton.Visibility = UndoButton.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
        FileTitle = null;
        FileNameText.Text = "No file open";
        FileInfoText.Text = "Open a video or audio file, or drop one onto this window.";
        FileNameText.ToolTip = null;
        FavoriteButton.Tag = false;
        MetadataGroup.Visibility = Visibility.Collapsed;
        _timeline.Reset(0);
        Timeline.HasMedia = false;
        Timeline.SetBookmarks([]);
        _crop.Reset(0, 0);
        LoopToggle.IsChecked = false;
        UpdateTrackHeader();
        UpdateUiState();
        UpdateViewSeek();
        TitleChanged?.Invoke();
        Root.Focus();
    }

    private async Task RefreshSourceAsync()
    {
        if (_closed || _loading || _deletingMedia || _replacingSource || _recheckingSource ||
            CurrentFile is not { } path) return;
        var stamp = _sourceStamp;
        bool wasUnavailable = _sourceState != EditorSourceState.Ready;
        if (!VerifySource(path)) return;
        if (!wasUnavailable && !_sourceSuspended) return;
        var media = _media;
        _recheckingSource = true;
        RetryMissingSourceButton.IsEnabled = false;
        try
        {
            if (wasUnavailable) await MediaInfo.ProbeAsync(path);
            if (_closed || !ReferenceEquals(media, _media) || _sourceStamp != stamp || !VerifySource(path)) return;
            SetSourceState(EditorSourceState.Ready);
            ReloadPreview();
            StartWaveforms();
            if (media != null) _ = LoadKeyframesAsync(media);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!_closed && ReferenceEquals(media, _media))
            {
                var state = EditorSource.Check(path, stamp, out _);
                SetSourceState(state == EditorSourceState.Ready ? EditorSourceState.Unreadable : state);
            }
        }
        finally
        {
            _recheckingSource = false;
            RetryMissingSourceButton.IsEnabled = true;
        }
    }
}
