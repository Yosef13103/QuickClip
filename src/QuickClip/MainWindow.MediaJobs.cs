using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuickClip.Media;
using QuickClip.Views;

namespace QuickClip;

public partial class MainWindow
{
    private readonly Dictionary<Guid, MenuItem> _mediaJobItems = [];
    private MenuItem? _mediaJobsEmpty;
    private (string? Path, bool Current) _mediaClipboard;

    private void RefreshMediaJobs()
    {
        var jobs = MediaJobs.Current.Snapshot();
        int active = jobs.Count(job => job.Active);
        var label = active > 0 ? $"Media jobs · {active}" : "Media jobs";
        if (!Equals(MediaJobsButton.Content, label)) MediaJobsButton.Content = label;
        if (MediaJobsButton.ContextMenu is not { IsOpen: true } menu) return;
        foreach (var id in _mediaJobItems.Keys.Where(id => !jobs.Any(job => job.Id == id)).ToArray())
        {
            menu.Items.Remove(_mediaJobItems[id]);
            _mediaJobItems.Remove(id);
        }
        _mediaJobsEmpty!.Visibility = jobs.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var job in jobs)
        {
            if (!_mediaJobItems.TryGetValue(job.Id, out var row))
            {
                _mediaJobItems.Add(job.Id, row = new MenuItem());
                menu.Items.Insert(1, row);
            }
            string progress = job.Active && job.Progress is { } value ? $" · {value:P0}" : "";
            string time = job.Elapsed.TotalSeconds >= 1 ? $" · {Fmt.Time(job.Elapsed.TotalSeconds, false)}" : "";
            string eta = job.Remaining is { } left ? $" · about {Fmt.Time(left.TotalSeconds, false)} left" : "";
            _mediaClipboard = (ClipSharing.LastCopied, ClipSharing.IsLastCopyCurrent);
            string copied = job.Share && job.Output is { } output
                ? _mediaClipboard == (output, true) ? " · on clipboard" : " · ready to copy"
                : "";
            row.Header = new TextBlock
            {
                Text = $"{job.Name} · {job.State}{progress}{time}{eta}{copied}",
                MaxWidth = 420, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var actions = (job.State, job.CanRetry);
            if (Equals(row.Tag, actions)) continue;
            row.Tag = actions;
            row.Items.Clear();
            if (job.Active)
                SharingDialogs.Add(row.Items, "Cancel", () => MediaJobs.Current.Cancel(job.Id));
            else if (job.Output is { } file)
            {
                if (job.Share)
                    SharingDialogs.Add(row.Items, "Copy again", () => _ = CopyJobOutputAsync(file));
                SharingDialogs.Add(row.Items, job.Share ? "Reveal for manual attachment" : "Reveal", () => RevealMedia(file));
            }
            else
            {
                if (job.Message is { } message)
                    row.Items.Add(new MenuItem { Header = new TextBlock { Text = message, MaxWidth = 360, TextWrapping = TextWrapping.Wrap }, IsEnabled = false });
                if (job.CanRetry) SharingDialogs.Add(row.Items, "Retry", () => _ = RetryMediaJobAsync(job.Id));
            }
        }
    }

    private void MediaJobs_Click(object sender, RoutedEventArgs e)
    {
        if (MediaJobsButton.ContextMenu == null)
        {
            var menu = new ContextMenu { PlacementTarget = MediaJobsButton, Placement = PlacementMode.Top, MaxHeight = 480 };
            _mediaJobsEmpty = new MenuItem { Header = "No media jobs this session", IsEnabled = false };
            menu.Items.Add(_mediaJobsEmpty);
            SharingDialogs.Add(menu.Items, "Open shared copies folder", () =>
            {
                Directory.CreateDirectory(ClipSharing.Folder);
                Process.Start(new ProcessStartInfo(ClipSharing.Folder) { UseShellExecute = true });
            });
            MediaJobsButton.ContextMenu = menu;
        }
        MediaJobsButton.ContextMenu.IsOpen = true;
        RefreshMediaJobs();
    }

    private async Task CopyJobOutputAsync(string path)
    {
        if (!await ClipSharing.CopyAgainAsync(path))
            ModalDialog.Show(this, "Could not copy the file. Use Reveal to attach it manually.", "Sharing");
        RefreshMediaJobs();
    }

    private async Task RetryMediaJobAsync(Guid id)
    {
        Task<MediaJobView>? completion;
        try { completion = MediaJobs.Current.Retry(id); }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException)
        {
            ModalDialog.Show(this, ex.Message, "Media jobs");
            return;
        }
        RefreshMediaJobs();
        if (completion != null) await ClipSharing.ReportCompletionAsync(completion, ClipSharing.Notify);
    }

    private static void RevealMedia(string path)
    {
        if (!File.Exists(path)) return;
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add($"/select,{path}");
        Process.Start(start);
    }
}
