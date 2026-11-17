using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    private (TextBox Box, ClipItem Clip, ClipAnnotationTarget Target)? _rename;
    private Task<bool>? _renameSave;

    private async Task LoadDraftRangesAsync()
    {
        try
        {
            var ranges = await PersonalLibrary.Current.ReadDraftRangesAsync(_clips, _lifetime.Token);
            foreach (var clip in _clips)
                clip.ApplyEditRange(ranges.TryGetValue(clip.Path, out var range) ? range : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not read Gallery edit ranges", ex);
            ShowStatus("Could not load saved edit durations.");
        }
    }

    internal void ApplyEditDraft(string path, ClipDraftRange? range)
    {
        if (_loading) _reloadQueued = true;
        var clip = _clips.FirstOrDefault(clip => SavedRecordingFolders.Paths.Equals(clip.Path, path));
        if (clip == null) return;
        if (range is { } draft)
        {
            var stamp = ClipMediaStamp.Read(clip.File);
            if (stamp.Length != draft.Source.Bytes || stamp.Created.Ticks != draft.Source.Created ||
                stamp.Modified.Ticks != draft.Source.Modified) return;
        }
        _hoverPreview.Stop();
        clip.ApplyEditRange(range);
    }

    internal async Task<bool> FlushAnnotationsAsync()
    {
        using var deadline = new CancellationTokenSource(SaveDeadline.Duration);
        if (_renameSave is { IsCompleted: false } pending && !await SaveDeadline.WaitAsync(pending, deadline.Token))
            return ShowRenameWaitFailure(deadline.Token);
        if (_rename is { } active && !await SaveDeadline.WaitAsync(CommitRenameAsync(active.Box), deadline.Token))
            return ShowRenameWaitFailure(deadline.Token);
        return await GalleryDetailsEditor.FlushAsync();
    }

    private bool ShowRenameWaitFailure(CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            Log.WarnSlowSave();
            ShowStatus("Saving the title is taking too long. Wait for storage and try again.");
        }
        return false;
    }

    private void RenameTitle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TextBox box }) BeginRename(box);
        e.Handled = true;
    }

    private void CancelRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: TextBox box } && _rename?.Box == box) EndRename(box);
        e.Handled = true;
    }

    private async void BeginRename(TextBox box)
    {
        if (!PersonalAnnotations || _editingAnnotations || _released || box.DataContext is not ClipItem clip) return;
        if (_rename?.Box == box) { box.Focus(); return; }
        if (!await FlushAnnotationsAsync() || !ReferenceEquals(box.DataContext, clip) || !box.IsLoaded || _released || !IsAvailable(clip)) return;
        _hoverPreview.Stop();
        _rename = (box, clip, ClipAnnotationTarget.For(clip));
        box.Text = clip.Title;
        box.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
    }

    private async void RenameTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || _rename?.Box != box) return;
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        if (e.Key == Key.Escape) EndRename(box);
        else await CommitRenameAsync(box);
    }

    private async void RenameTitle_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box && _rename?.Box == box) await CommitRenameAsync(box);
    }

    private async void RenameTitle_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && _rename?.Box == box) await CommitRenameAsync(box);
    }

    private void EndRename(TextBox box)
    {
        _rename = null;
        box.Visibility = Visibility.Collapsed;
    }

    private Task<bool> CommitRenameAsync(TextBox box)
    {
        if (_renameSave is { IsCompleted: false } pending) return pending;
        if (_rename is not { } active || active.Box != box) return Task.FromResult(true);
        string title = box.Text.Trim();
        EndRename(box);
        if (title == active.Clip.Title) return Task.FromResult(true);
        return _renameSave = SaveRenameAsync(active, title);
    }

    private async Task<bool> SaveRenameAsync((TextBox Box, ClipItem Clip, ClipAnnotationTarget Target) active, string title)
    {
        using var timeout = new CancellationTokenSource(SaveDeadline.Duration);
        using var operation = Log.Begin(DiagnosticCategory.Editor, "Gallery title save");
        var target = active.Target;
        try
        {
            var draft = target.Details with { Title = title.Length == 0 ? null : title };
            var result = await AnnotationActions.SaveDraftAsync(target, draft, timeout.Token);
            if (result.Details != null)
            {
                operation?.Complete();
                ApplyClipDetails(result.Path, result.Details);
                ShowStatus("Clip title saved.");
                return true;
            }
            ShowStatus(result.Error == null ? "Could not rename the clip." : $"Could not rename the clip. {result.Error}");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            operation?.Cancel();
            ShowStatus("Storage did not respond in time. Retry the title change.");
        }
        catch (Exception ex)
        {
            ShowStatus("Could not rename the clip.");
            Log.Failure(DiagnosticCategory.Library, "Could not rename clip", ex);
        }
        if (!_released && _rename == null && active.Box.IsLoaded && ReferenceEquals(active.Box.DataContext, active.Clip))
        {
            _rename = active;
            active.Box.Visibility = Visibility.Visible;
        }
        return false;
    }

    private static bool IsCardControl(object source, object card)
    {
        for (var node = source as DependencyObject; node != null && node != card;)
        {
            if (node is ButtonBase or TextBox) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

}
