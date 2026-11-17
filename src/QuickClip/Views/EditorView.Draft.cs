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
    private bool _editChangedDuringWrite, _editSaveTimedOut;
    private bool _resettingDraft, _exportPreparing, _leavingClip;
    private Task? _recoveryWrite, _recoveryCleanup;
    private bool _restoredRecovery;

    internal event Action<string, ClipDraftRange?>? EditDraftSaved;

    private void OnDetailsSaved(string path, ClipDetails details)
    {
        if (string.Equals(CurrentFile, path, StringComparison.OrdinalIgnoreCase)) ShowClipTitle(details);
        ClipDetailsSaved?.Invoke(path, details);
    }

    internal void ApplyCommittedDetails(string path, ClipDetails details)
    {
        if (_closed || !SavedRecordingFolders.Paths.Equals(CurrentFile, path)) return;
        if (details.IndexId == null && !MetadataEditor.HasDraft && !MetadataEditor.IsSaving)
        {
            _ = RefreshMetadataAsync(); // Legacy sidecars have no revision; re-read the verified current file.
            return;
        }
        if (MetadataEditor.ApplyCommittedDetails(details)) ShowClipTitle(details);
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
        _restoredRecovery = false;
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

    private async Task<bool> CheckpointUnsavedAsync()
    {
        if (!AppProfile.IsPersonal || _sourceStamp is not { Identity: not null } stamp ||
            (_recoveryWrite is { IsCompleted: false })) return false;
        var details = MetadataEditor.CaptureRecovery();
        var edit = _defaultEditDraft != null ? CaptureEditDraft() : null;
        string? serialized = edit?.Serialize();
        if (serialized == _defaultEditPayload) serialized = null;
        if (serialized == _storedEditPayload) edit = null;
        if (details == null && edit == null) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var operation = Log.Begin(DiagnosticCategory.Editor, "Editor recovery checkpoint");
        var snapshot = new EditorRecovery(stamp, details, _storedEditPayload, edit);
        var cleanup = _recoveryCleanup;
        var token = timeout.Token;
        _recoveryWrite = Task.Run(async () =>
        {
            if (cleanup != null) await cleanup.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            snapshot.Write(token);
        });
        try
        {
            await _recoveryWrite.WaitAsync(timeout.Token);
            _restoredRecovery = false;
            operation?.Complete();
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            operation?.Cancel();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            operation?.Fail(ex.HResult);
            return false;
        }
    }

    private void OfferRecovery(EditorRecovery? recovery)
    {
        _restoredRecovery = false;
        if (recovery == null) return;
        bool detailsMatch = recovery.Details == null || MetadataEditor.RestoreRecovery(recovery.Details, apply: false);
        bool editMatches = recovery.Edit == null || _draftReadError == null &&
            (_storedEditPayload == recovery.PreviousEdit || _storedEditPayload == recovery.Edit.Serialize());
        if (!detailsMatch || !editMatches)
        {
            DraftStatus.Text = "Recovery is available, but saved changes conflict. The recovery copy was kept.";
            return;
        }
        if (!ModalDialog.ConfirmAction(OwnerWindow, "Restore the unsaved changes from the previous session?",
            "Recover editor changes", "Restore changes", "Keep saved version", destructive: false)) return;

        if (recovery.Details != null) MetadataEditor.RestoreRecovery(recovery.Details, apply: true);
        if (recovery.Edit != null && !RestoreEditDraft(recovery.Edit))
        {
            DraftStatus.Text = "Could not restore the edit draft. The recovery copy was kept.";
            return;
        }
        _restoredRecovery = true;
        DraftStatus.Text = "Recovered changes. Review them before leaving this clip.";
    }

    private void RemoveRestoredRecovery()
    {
        if (!_restoredRecovery || _sourceStamp is not { } stamp) return;
        _restoredRecovery = false;
        _recoveryCleanup = Task.Run(() =>
        {
            try { EditorRecovery.Remove(stamp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Failure(DiagnosticCategory.Editor, "Could not remove old editor recovery", ex);
            }
        });
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

    private Task<bool> SaveEditDraftAsync(CancellationToken token = default)
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
        _editSaveTimedOut = false;
        _editWrite = PersistEditDraftAsync(path, stamp, payload == null ? null : state, payload, token);
        return _editWrite;
    }

    private async Task<bool> PersistEditDraftAsync(string path, FileStamp stamp, ClipEditDraft? state, string? payload, CancellationToken token)
    {
        using var operation = Log.Begin(DiagnosticCategory.Editor, "Edit draft save");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SaveDeadline.Duration);
        await Task.Yield();
        DraftStatus.Text = "Saving edits…";
        try
        {
            await PersonalLibrary.Current.SaveEditDraftAsync(path, stamp, _storedEditPayload, state, timeout.Token);
            operation?.Complete();
            _storedEditPayload = payload;
            DraftStatus.Text = state == null ? "" : "Edits saved";
            EditDraftSaved?.Invoke(path, state == null ? null : new(stamp, state.Start, state.End));
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            operation?.Cancel();
            DraftStatus.Text = "Storage did not respond in time. Your edits are still here.";
            return false;
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
        if (_editSaveTimedOut && _editWrite is { IsCompleted: false }) return false;
        using var deadline = new CancellationTokenSource(SaveDeadline.Duration);
        bool saved = (_editWrite is not { } pending || await SaveDeadline.WaitAsync(pending, deadline.Token)) &&
            await SaveDeadline.WaitAsync(SaveEditDraftAsync(deadline.Token), deadline.Token);
        if (deadline.IsCancellationRequested)
        {
            _editSaveTimedOut = _editWrite is { IsCompleted: false };
            Log.WarnSlowSave();
            DraftStatus.Text = "Storage is taking too long. Your edits are still here.";
        }
        return saved;
    }

    internal bool CanLeaveAfterSaveFailure => !_leavingClip && !_resettingDraft && !_loading;

    internal async Task<bool> FlushForNavigationAsync()
    {
        if (!CanLeaveAfterSaveFailure) return false;
        _leavingClip = true;
        try
        {
            return await FlushClipAsync();
        }
        finally { _leavingClip = false; }
    }

    private async Task<bool> FlushClipAsync()
    {
        bool enabled = IsEnabled;
        IsEnabled = false;
        try
        {
            _player?.CommandAsync("set", "pause", "yes");
            bool details = await MetadataEditor.FlushAsync();
            bool draft = await FlushEditDraftAsync();
            if (details && draft) RemoveRestoredRecovery();
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

    private async Task<bool> TryLeaveClipAsync(bool closing = false)
    {
        if (_leavingClip) return false;
        _leavingClip = true;
        try
        {
            bool saved = await FlushClipAsync();
            if (!closing && (MetadataEditor.IsSaving || _editWrite is { IsCompleted: false })) return false;
            bool transient = _tracks.Any(track => track.IsSound) ||
                (_openedDraft != null && _openedDraft != DraftSignature());
            if (saved && !transient) return true;
            if (!saved && closing && await CheckpointUnsavedAsync())
            {
                if (!transient) return true;
                return ModalDialog.ConfirmAction(OwnerWindow,
                    "Pending metadata and timeline edits were kept for recovery. Added sounds are only available for this session. Close anyway?",
                    "Close editor", "Close editor", "Keep editing", destructive: true);
            }
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
            using var timeout = new CancellationTokenSource(SaveDeadline.Duration);
            if (_editWrite is { IsCompleted: false } pending && !await SaveDeadline.WaitAsync(pending, timeout.Token))
            {
                DraftStatus.Text = "Could not reset edits while the previous save is settling.";
                return;
            }
            if (_draftReadError != null)
            {
                DraftStatus.Text = _draftReadError;
                return;
            }
            if (_storedEditPayload != null)
            {
                _editWrite = PersistEditDraftAsync(path, stamp, null, null, timeout.Token);
                if (!await SaveDeadline.WaitAsync(_editWrite, timeout.Token))
                {
                    _editSaveTimedOut = _editWrite is { IsCompleted: false } && timeout.IsCancellationRequested;
                    DraftStatus.Text = "Could not reset saved edits.";
                    return;
                }
            }
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
