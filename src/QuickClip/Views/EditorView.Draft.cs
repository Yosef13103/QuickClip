using System.Windows;
using System.Windows.Threading;
using QuickClip.Controls;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class EditorView
{
    private readonly DispatcherTimer _draftTimer;
    private ClipEditDraft? _defaultEditDraft;
    private string? _defaultEditPayload, _storedEditPayload, _draftReadError;
    private Task<bool>? _editWrite;
    private bool _editChangedDuringWrite;
    private bool _resettingDraft, _exportPreparing, _leavingClip;

    internal event Action<string, ClipDraftRange?>? EditDraftSaved;

    private void OnDetailsSaved(string path, ClipDetails details)
    {
        if (string.Equals(CurrentFile, path, StringComparison.OrdinalIgnoreCase)) ShowClipTitle(details);
        ClipDetailsSaved?.Invoke(path, details);
    }

    private async Task<StoredClipDraft?> ReadEditDraftAsync(string path, FileStamp stamp)
    {
        _draftReadError = null;
        if (!AppProfile.IsPersonal) return null;
        if (ClipLibrary.IsMomentsPath(path))
        {
            _draftReadError = "Saving edits is unavailable for the Moments archive.";
            return null;
        }
        try { return await PersonalLibrary.Current.ReadEditDraftAsync(path, stamp); }
        catch (Exception ex)
        {
            _draftReadError = ex is InvalidDataException or IOException
                ? ex.Message : "Could not load saved edits.";
            Log.Failure(DiagnosticCategory.Library, "Could not load clip edits", ex);
            return null;
        }
    }

    private void ClearEditDraft()
    {
        _draftTimer.Stop();
        _defaultEditDraft = null;
        _defaultEditPayload = _storedEditPayload = _draftReadError = null;
        DraftStatus.Text = "";
        ResetEditsButton.Visibility = Visibility.Collapsed;
    }

    private void SetUpEditDraft(StoredClipDraft? draft)
    {
        _draftTimer.Stop();
        _defaultEditDraft = AppProfile.IsPersonal ? CaptureEditDraft() : null;
        _defaultEditPayload = _defaultEditDraft?.Serialize();
        _storedEditPayload = draft?.Payload;
        bool restored = draft?.State is { } state && RestoreEditDraft(state);
        DraftStatus.Text = _draftReadError ??
            (restored ? "Edits restored" : draft != null ? "Could not restore saved edits. You can reset them." : "");
        ResetEditsButton.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
    }

    private ClipEditDraft CaptureEditDraft()
    {
        var rect = _crop.Rect;
        return new(_timeline.In, _timeline.Out, _timeline.Position, LoopToggle.IsChecked == true,
            _tracks.Where(track => !track.IsSound)
                .Select(track => new DraftTrack(track.Info.Index, track.Include, track.Volume, track.Name)).ToArray(),
            _crop.SourceWidth > 0 && _crop.SourceHeight > 0
                ? new(_crop.Enabled, rect.X, rect.Y, rect.Width, rect.Height, Math.Max(0, AspectCombo.SelectedIndex))
                : null);
    }

    private bool RestoreEditDraft(ClipEditDraft draft)
    {
        double duration = _timeline.Duration;
        if (draft.End > duration + 0.001 || (draft.Start > 0 && draft.End - draft.Start < TimelineState.MinSelection - 0.000001))
            return false;
        _timeline.ResetSelection();
        if (draft.Start > 0) _timeline.SetIn(draft.Start);
        if (draft.End < duration && duration >= TimelineState.MinSelection) _timeline.SetOut(draft.End);
        _timeline.SetPosition(draft.Position);
        LoopToggle.IsChecked = draft.Loop;
        var tracks = draft.Tracks.ToDictionary(track => track.Index);
        foreach (var track in _tracks.Where(track => !track.IsSound))
        {
            if (!tracks.TryGetValue(track.Info.Index, out var saved)) continue;
            track.Include = saved.Include;
            track.Volume = saved.Volume;
            track.Name = saved.Name;
            track.Solo = false;
        }
        if (draft.Crop is { } crop && _crop.SourceWidth >= 16 && _crop.SourceHeight >= 16)
        {
            CropCheck.IsChecked = crop.Enabled;
            AspectCombo.SelectedIndex = crop.Aspect < AspectCombo.Items.Count ? crop.Aspect : 0;
            _crop.SetRect(crop.X, crop.Y, crop.Width, crop.Height);
        }
        UpdateTimeText();
        return true;
    }

    private void SetPreviewStart() => _player?.SetProperty("start",
        _timeline.Position > 0 ? Fmt.Num(_timeline.Position, "0.######") : "none");

    private void QueueEditDraft()
    {
        if (_defaultEditDraft == null || _closed || _loading || _deletingMedia || _resettingDraft ||
            _leavingClip || _exportCts != null || !IsVisible || !SourceReady) return;
        if (_editWrite != null) _editChangedDuringWrite = true;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private Task<bool> SaveEditDraftAsync()
    {
        _draftTimer.Stop();
        if (_editWrite != null) return _editWrite;
        if (_defaultEditDraft == null || CurrentFile is not { } path || _sourceStamp is not { } stamp)
            return Task.FromResult(true);

        var state = CaptureEditDraft();
        string serialized = state.Serialize();
        string? payload = serialized == _defaultEditPayload ? null : serialized;
        if (payload == _storedEditPayload) return Task.FromResult(true);
        if (_draftReadError != null)
        {
            DraftStatus.Text = _draftReadError;
            return Task.FromResult(false);
        }

        // Reuse the same write if a timer and a navigation request overlap.
        _editWrite = PersistEditDraftAsync(path, stamp, payload == null ? null : state, payload);
        return _editWrite;
    }

    private async Task<bool> PersistEditDraftAsync(string path, FileStamp stamp, ClipEditDraft? state, string? payload)
    {
        await Task.Yield();
        DraftStatus.Text = "Saving edits…";
        try
        {
            await PersonalLibrary.Current.SaveEditDraftAsync(path, stamp, _storedEditPayload, state);
            _storedEditPayload = payload;
            DraftStatus.Text = state == null ? "" : "Edits saved";
            EditDraftSaved?.Invoke(path, state == null ? null : new(stamp, state.Start, state.End));
            return true;
        }
        catch (Exception ex)
        {
            DraftStatus.Text = ex is LibraryConflictException ? ex.Message : "Could not save edits.";
            Log.Failure(DiagnosticCategory.Library, "Could not save clip edits", ex);
            return false;
        }
        finally
        {
            _editWrite = null;
            if (_editChangedDuringWrite)
            {
                _editChangedDuringWrite = false;
                QueueEditDraft();
            }
        }
    }

    private async Task<bool> FlushEditDraftAsync()
    {
        _draftTimer.Stop();
        if (_editWrite is { } pending) await pending;
        return await SaveEditDraftAsync();
    }

    internal async Task<bool> FlushForNavigationAsync()
    {
        if (_leavingClip || _resettingDraft || _loading) return false;
        _leavingClip = true;
        try
        {
            if (await FlushClipAsync()) return true;
            return ModalDialog.ConfirmAction(OwnerWindow,
                "Some changes could not be saved. They will stay open in the editor for this session.",
                "Could not save changes", "Leave editor", "Keep editing", destructive: false);
        }
        finally { _leavingClip = false; }
    }

    private async Task<bool> FlushClipAsync()
    {
        bool enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            _player?.SetProperty("pause", true);
            bool details = await MetadataEditor.FlushAsync();
            bool draft = await FlushEditDraftAsync();
            return details && draft;
        }
        finally { IsEnabled = enabled; }
    }

    private async Task<bool> FlushMetadataForExportAsync()
    {
        if (await MetadataEditor.FlushAsync()) return true;
        _viewMode = false;
        ExitFullscreen();
        ApplyMode();
        MetadataGroup.IsExpanded = true;
        MetadataEditor.ShowSaveReminder();
        return false;
    }

    private async Task<bool> TryLeaveClipAsync()
    {
        if (_leavingClip) return false;
        _leavingClip = true;
        try
        {
            bool saved = await FlushClipAsync();
            bool transient = _tracks.Any(track => track.IsSound) ||
                (_openedDraft != null && _openedDraft != DraftSignature());
            if (saved && !transient) return true;
            string message = !saved
                ? "Some changes could not be saved. Discard the unsaved changes?"
                : "Some edit settings are only available for this session. Discard them?";
            return ModalDialog.ConfirmAction(OwnerWindow, message, "Unsaved changes",
                "Discard changes", "Keep editing", destructive: true);
        }
        finally { _leavingClip = false; }
    }

    private async void ResetEdits_Click(object sender, RoutedEventArgs e)
    {
        if (_defaultEditDraft is not { } defaults || _resettingDraft || _loading || _deletingMedia ||
            _leavingClip || _exportPreparing || _exportCts != null || !SourceReady ||
            CurrentFile is not { } path || _sourceStamp is not { } stamp) return;
        if (!ModalDialog.ConfirmAction(OwnerWindow, "Reset the trim, playhead, crop and audio edits to the original clip?",
            "Reset edits", "Reset edits", "Keep editing", destructive: true)) return;

        _resettingDraft = true;
        _draftTimer.Stop();
        UpdateUiState();
        try
        {
            if (_editWrite is { } pending) await pending;
            if (_draftReadError != null)
            {
                DraftStatus.Text = _draftReadError;
                return;
            }
            if (_storedEditPayload != null)
                await PersonalLibrary.Current.SaveEditDraftAsync(path, stamp, _storedEditPayload, null);
            _storedEditPayload = null;
            foreach (var sound in _tracks.Where(track => track.IsSound).ToArray()) RemoveSound(sound);
            _timeline.Fit();
            RestoreEditDraft(defaults);
            ApplyPreviewVolumes();
            Seek(0);
            DraftStatus.Text = "Edits reset";
            EditDraftSaved?.Invoke(path, null);
        }
        catch (Exception ex)
        {
            DraftStatus.Text = "Could not reset saved edits.";
            Log.Failure(DiagnosticCategory.Library, "Could not reset clip edits", ex);
        }
        finally
        {
            _resettingDraft = false;
            UpdateUiState();
            await OpenPendingFileAsync();
        }
    }

}
