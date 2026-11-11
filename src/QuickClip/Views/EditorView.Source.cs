using System.Windows;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Views;

public partial class EditorView
{
    private FileStamp? _sourceStamp;
    private EditorSourceState _sourceState;
    private bool _sourceSuspended, _recheckingSource, _replacingSource;
    private Window? _sourceWindow;
    private bool SourceReady => _sourceState == EditorSourceState.Ready && !_sourceSuspended;

    private void AttachSourceEvents()
    {
        ClipDeletion.Current.Releasing += OnSourceReleasing;
        ClipDeletion.Current.Changed += OnSourceDeletionChanged;
        _sourceWindow = Window.GetWindow(this);
        if (_sourceWindow != null) _sourceWindow.Activated += OnSourceWindowActivated;
    }

    private void DetachSourceEvents()
    {
        ClipDeletion.Current.Releasing -= OnSourceReleasing;
        ClipDeletion.Current.Changed -= OnSourceDeletionChanged;
        if (_sourceWindow != null) _sourceWindow.Activated -= OnSourceWindowActivated;
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
    }

    private void StopSourcePreview()
    {
        _waveCts?.Cancel();
        _keyframeCts?.Cancel();
        _soundCts?.Cancel();
        _soundPreviewTimer.Stop();
        _audioSyncTimer.Stop();
        _paused = true;
        _player?.Command("stop");
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
        Video.Visibility = ready ? Visibility.Visible : Visibility.Hidden;
        UnavailableSource.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        Timeline.HasMedia = ready && _media != null;
        UpdateUiState();
        UpdateViewSeek();
    }

    private bool VerifySource(string path)
    {
        var state = EditorSource.Check(path, _sourceStamp, out _);
        if (state == EditorSourceState.Ready) return true;
        SetSourceState(state);
        return false;
    }

    private async void RetrySource_Click(object sender, RoutedEventArgs e) => await RefreshSourceAsync();

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
        finally { _recheckingSource = false; }
    }
}
