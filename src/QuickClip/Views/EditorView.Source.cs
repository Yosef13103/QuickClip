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
    private bool _previewReady, _probingSource;
    private long _previewEntry;
    private DispatcherTimer? _previewProgressTimer;

    private readonly TaskCompletionSource<bool> _previewInitialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<bool>? _preparedPreview;

    internal async Task<bool> PreparePreviewAsync(string path, CancellationToken token)
    {
        if (!await _previewInitialization.Task.WaitAsync(token)) return false;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparedPreview = ready;
        try
        {
            await OpenAsync(path, token);
            token.ThrowIfCancellationRequested();
            if (!SourceReady || !EditorSource.MatchesLoadedPreview(CurrentFile, path)) return false;
            return _previewReady || _previewEntry != 0 && await ready.Task.WaitAsync(token) &&
                SourceReady && EditorSource.MatchesLoadedPreview(CurrentFile, path);
        }
        finally { _preparedPreview = null; }
    }

    private void ShowPreviewLoading()
    {
        Video.Visibility = Visibility.Hidden;
        EmptySourceHint.Text = "Loading preview…";
        EmptySourceHint.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation) return;
        _previewProgressTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) =>
            {
                _previewProgressTimer!.Stop();
                PreviewProgress.IsIndeterminate = true;
                PreviewProgress.Visibility = Visibility.Visible;
            }, Dispatcher);
        _previewProgressTimer.Start();
    }

    private void HidePreviewProgress()
    {
        _previewProgressTimer?.Stop();
        PreviewProgress.IsIndeterminate = false;
        PreviewProgress.Visibility = Visibility.Collapsed;
    }

    private void LoadNativePreview(string path)
    {
        _previewEntry = 0;
        // loadfile enqueues opening the media. The playlist entry is available even while
        // demuxing; its ID distinguishes successive loads of the same filename.
        if (_player?.Command("loadfile", path, "replace") == true &&
            long.TryParse(_player.GetString("playlist/0/id"), out _previewEntry)) return;
        HidePreviewProgress();
        EmptySourceHint.Text = "Preview not available for this file";
        _preparedPreview?.TrySetResult(false);
    }

    private void OnMpvPreviewReadyOrFailed(long entryId, int error)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _probingSource || _pendingFile != null || !SourceReady || _media == null ||
                entryId == 0 || entryId != _previewEntry) return;
            HidePreviewProgress();
            if (error < 0)
            {
                _previewReady = false;
                _previewEntry = 0;
                PlayButton.IsEnabled = false;
                Video.Visibility = Visibility.Hidden;
                EmptySourceHint.Visibility = Visibility.Visible;
                EmptySourceHint.Text = "Preview not available for this file";
                _preparedPreview?.TrySetResult(false);
                Log.Event(DiagnosticCategory.Editor, "mpv playback ended with an error", DiagnosticSeverity.Error, error);
                return;
            }

            if (!EditorSource.MatchesLoadedPreview(_player?.GetString("path"), _media.Path)) return;
            _previewReady = true;
            _preparedPreview?.TrySetResult(true);
            _idle = false;
            Video.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = true;
            EmptySourceHint.Visibility = Visibility.Collapsed;
            Video.DisableChildWindows();
            DeleteStalePreviews();
            ApplyPreviewVolumes();
            ApplyLoop();
            UpdateOsdGeometry();
            QueueNativePreviewRepaint();
        });
    }
    private bool _restoreRepaintQueued;
    private bool _sourceEventsAttached;
    private Window? _sourceWindow;
    private bool SourceReady => _sourceState == EditorSourceState.Ready && !_sourceSuspended;

    private void AttachSourceEvents()
    {
        // Subscribe exactly once even if the host's visual tree is reattached.
        if (_sourceEventsAttached) return;
        _sourceEventsAttached = true;
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
        if (!_sourceEventsAttached) return;
        _sourceEventsAttached = false;
        ClipDeletion.Current.Releasing -= OnSourceReleasing;
        ClipDeletion.Current.Changed -= OnSourceDeletionChanged;
        if (_sourceWindow != null)
        {
            _sourceWindow.Activated -= OnSourceWindowActivated;
            _sourceWindow.StateChanged -= OnSourceWindowStateChanged;
            _sourceWindow.IsVisibleChanged -= OnSourceWindowVisibilityChanged;
        }
        _sourceWindow = null;
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
        if (_restoreRepaintQueued || _closed || !IsVisible || _sourceWindow is not { IsVisible: true } window ||
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
                {
                    // Only update mpv's letterbox after its HWND has been laid out.
                    Video.RefreshAfterRestore();
                    _player?.RefreshTheme();
                    Video.RefreshAfterRestore();
                }
            }), DispatcherPriority.ContextIdle)), DispatcherPriority.Loaded);
    }

    private void StopSourcePreview(bool keepLoading = false)
    {
        _waveCts?.Cancel();
        _keyframeCts?.Cancel();
        _soundCts?.Cancel();
        _soundPreviewTimer.Stop();
        _audioSyncTimer.Stop();
        _paused = true;
        _previewReady = false;
        _previewEntry = 0;
        if (!keepLoading) HidePreviewProgress();
        PlayButton.IsEnabled = false;
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
        Video.Visibility = ready && _media != null && _previewReady && !_probingSource
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

    private async void ClearUnavailableSource_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal || SourceReady || _media == null || _closed || _loading ||
            _deletingMedia || _exportCts != null || _recheckingSource || MetadataEditor.IsSaving)
            return;
        if (!await TryLeaveClipAsync()) return;

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
        ClearEditDraft();
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

    internal async Task RefreshDisplayedClipAsync()
    {
        if (_leavingClip || _loading || _resettingDraft || _closed || MetadataEditor.IsSaving ||
            _editWrite is { IsCompleted: false }) return;
        await RefreshSourceAsync();
        await RefreshMetadataAsync();
    }

    internal Task RefreshMetadataOnReturnAsync() => RefreshMetadataAsync();

    private async Task RefreshMetadataAsync()
    {
        if (!AppProfile.IsPersonal || !SourceReady || _closed || _loading || CurrentFile is not { } path ||
            _sourceStamp is not { } stamp || MetadataEditor.HasDraft || MetadataEditor.IsSaving) return;
        var initial = MetadataEditor.AnnotationTarget?.Details;
        using var timeout = new CancellationTokenSource(SaveDeadline.Duration);
        try
        {
            var read = ReadMetadataAsync(path, stamp, timeout.Token);
            var details = await read.WaitAsync(timeout.Token);
            if (details != null && !_closed && _sourceStamp == stamp &&
                ReferenceEquals(initial, MetadataEditor.AnnotationTarget?.Details) &&
                MetadataEditor.ApplyCommittedDetails(details))
                ShowClipTitle(details);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Failure(DiagnosticCategory.Editor, "Could not refresh clip details", ex);
        }
    }

    private async Task<ClipDetails?> ReadMetadataAsync(string path, FileStamp stamp, CancellationToken token = default,
        DateTimeOffset? embeddedCreationTime = null)
    {
        var details = await PersonalLibrary.Current.ReadAsync(path, token);
        var folder = _settings.ClipsFolder;
        var embedded = embeddedCreationTime ?? (SavedRecordingFolders.Paths.Equals(CurrentFile, path) ? _media?.EmbeddedCreationTime : null);
        return await Task.Run(() =>
        {
            if (EditorSource.Check(path, stamp, out _) != EditorSourceState.Ready) return null;
            var sidecar = ClipDetailsStore.Read(new FileInfo(path), ClipLibrary.InitialGame(folder, path), embedded);
            return details == null ? sidecar : QuickClip.Recording.RecordingAudioMix.WithSavedDefaults(details, sidecar);
        }, token);
    }

    private async Task RefreshSourceAsync()
    {
        if (_closed || _loading || _deletingMedia || _replacingSource || _recheckingSource ||
            CurrentFile is not { } path) return;
        var stamp = _sourceStamp;
        bool wasUnavailable = _sourceState != EditorSourceState.Ready;
        var media = _media;
        _recheckingSource = true;
        RetryMissingSourceButton.IsEnabled = false;
        try
        {
            var state = await Task.Run(() => EditorSource.Check(path, stamp, out _));
            if (_closed || _loading || !ReferenceEquals(media, _media) || _sourceStamp != stamp) return;
            if (state != EditorSourceState.Ready)
            {
                SetSourceState(state);
                return;
            }
            if (!wasUnavailable && !_sourceSuspended) return;
            if (wasUnavailable)
            {
                await MediaInfo.ProbeAsync(path);
                state = await Task.Run(() => EditorSource.Check(path, stamp, out _));
            }
            if (_closed || _loading || !ReferenceEquals(media, _media) || _sourceStamp != stamp) return;
            SetSourceState(state);
            if (state != EditorSourceState.Ready) return;
            ReloadPreview();
            StartWaveforms();
            if (media != null) _ = LoadKeyframesAsync(media);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (!_closed && ReferenceEquals(media, _media))
            {
                var state = await Task.Run(() => EditorSource.Check(path, stamp, out _));
                if (!_closed && !_loading && ReferenceEquals(media, _media) && _sourceStamp == stamp)
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
