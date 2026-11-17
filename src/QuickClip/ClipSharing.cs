using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text.Json;
using QuickClip.Library;
using QuickClip.Gallery;
using System.Windows;
using QuickClip.Media;
using QuickClip.Shell;
using QuickClip.Views;

namespace QuickClip;

internal static class ClipSharing
{
    private static long _lastClipboardRequest;
    private static uint _lastCopySequence;

    internal static string Folder => AppProfile.SharedDirectory;
    internal static string? LastCopied { get; private set; }
    internal static bool IsLastCopyCurrent => LastCopied != null && GetClipboardSequenceNumber() == _lastCopySequence;
    internal static void IgnorePendingClipboardCopies() => ++_lastClipboardRequest;

    internal static async Task ShareAsync(string clip, SharingProfile? profile = null, ExportRequest? edits = null)
    {
        var settings = AppSettings.Current;
        profile ??= SharingProfiles.Default(settings);
        double limit = profile.LimitMB;
        bool motionBlur = settings.ShareMotionBlur;
        long ticket = ++_lastClipboardRequest;
        uint sequence = GetClipboardSequenceNumber();
        try
        {
            var sources = MediaProcessing.Sources(clip, edits?.Tracks);
            string name = Path.GetFileNameWithoutExtension(clip);
            string dir = Path.Combine(Folder, "share-" + Guid.NewGuid().ToString("N"));
            string output = Path.Combine(dir, name + ".mp4");
            var submission = MediaJobs.Current.Submit(new($"{name} · {profile.Name}", true, sources, output,
                async (job, ct) =>
                {
                    ReserveShareFolder(dir);
                    try
                    {
                        string encoder = ShareCopy.Encoder(await VideoEncoders.GetAvailableAsync().WaitAsync(ct));
                        string published = await MediaProcessing.PublishAsync(output, ".mp4", null, async staging =>
                        {
                            var plan = edits == null
                                ? ShareCopy.Plan(await MediaInfo.ProbeAsync(clip, ct, localOnly: true), staging, encoder, limit, motionBlur: motionBlur)
                                : ShareCopy.Plan(edits with { OutputPath = staging }, encoder, limit, allowCopy: true, motionBlur);
                            long ceiling = TargetSizeExport.Ceiling(plan, limit);
                            MediaProcessing.CheckFreeSpace(dir, ceiling);
                            await TargetSizeExport.RunAsync(plan, ceiling, "libx264",
                                new ProgressCallback<(int Attempt, double Progress)>(p => job.Progress(p.Attempt, p.Progress)),
                                job.Validating, ct, job);
                        }, ct);
                        RecordRetention(published, publishedByJob: true);
                        return published;
                    }
                    finally
                    {
                        try { Directory.Delete(dir); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }));
            var result = await submission.Completion;
            if (result.Output is { } file && Application.Current != null)
            {
                bool copied = await TryCopyAsync(file, () => ticket == _lastClipboardRequest && GetClipboardSequenceNumber() == sequence);
                App.Host?.NotifyShareReady(copied && IsLastCopyCurrent && LastCopied == file);
            }
        }
        catch (Exception ex) when (ex is ExportException or IOException or UnauthorizedAccessException)
        {
            ModalDialog.Show(Application.Current?.MainWindow, ex.Message, "Can't share clip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static async Task<bool> CopyAgainAsync(string path)
    {
        long ticket = ++_lastClipboardRequest;
        uint sequence = GetClipboardSequenceNumber();
        bool copied = await TryCopyAsync(path, () => ticket == _lastClipboardRequest && GetClipboardSequenceNumber() == sequence);
        if (copied) App.Host?.NotifyShareReady(true);
        return copied;
    }

    private static async Task<bool> TryCopyAsync(string path, Func<bool> stillRequested)
    {
        if (!stillRequested() || !File.Exists(path)) return false;
        try
        {
            if (!ShellClipboard.TryCopyFile(path, stillRequested)) return false;
            LastCopied = path;
            _lastCopySequence = GetClipboardSequenceNumber();
            RecordRetention(path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Failure(DiagnosticCategory.General, "Shell copy unavailable; trying file drop", ex);
        }

        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        for (int attempt = 1; attempt <= 10 && stillRequested(); attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                LastCopied = path;
                _lastCopySequence = GetClipboardSequenceNumber();
            RecordRetention(path);
                return true;
            }
            catch (ExternalException) { await Task.Delay(100); }
        }
        return false;
    }

    private static readonly object ShareStorage = new();
    private static string RetentionJournal => Path.Combine(AppProfile.LocalDirectory, "share-retention");
    private sealed record ShareReceipt(string Path, FileStamp Stamp, DateTimeOffset UsedAt);

    internal static bool RetentionExpired(DateTimeOffset lastUse, DateTimeOffset now) =>
        lastUse <= now && now - lastUse >= TimeSpan.FromDays(1);

    internal static bool OwnedSharePath(string root, string path, string key)
    {
        if (!Guid.TryParseExact(key, "N", out _) || !Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)),
            Path.Combine(Path.GetFullPath(root), "share-" + key), StringComparison.OrdinalIgnoreCase);
    }

    private static void RecordRetention(string path, bool publishedByJob = false)
    {
        lock (ShareStorage)
        {
            try
            {
                string dir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
                if (!dir.StartsWith("share-", StringComparison.Ordinal) || !OwnedSharePath(Folder, path, dir[6..])) return;
                var stamp = MediaSource.Capture(path).Stamp;
                if (stamp.Identity == null) return;
                MediaProcessing.RequireSafeDirectory(RetentionJournal);
                Directory.CreateDirectory(RetentionJournal);
                string receipt = Path.Combine(RetentionJournal, dir[6..] + ".json");
                // Recopy extends an existing receipt, but never adopts changed or untracked files.
                if (File.Exists(receipt))
                {
                    if (LibraryFiles.InspectPath(receipt, directory: false) != ClipAvailability.Available ||
                        new FileInfo(receipt).Length > 16_384) return;
                    var previous = JsonSerializer.Deserialize<ShareReceipt>(File.ReadAllText(receipt));
                    if (previous == null || previous.Stamp != stamp ||
                        !string.Equals(previous.Path, path, StringComparison.OrdinalIgnoreCase)) return;
                }
                else
                {
                    // Only publication creates a receipt. Recopying old untracked files is not authorization.
                    // The worker calls this before completing the job; UI copies only renew tracked receipts.
                    if (!publishedByJob) return;
                }
                string staged = receipt + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        JsonSerializer.Serialize(stream, new ShareReceipt(path, stamp, DateTimeOffset.UtcNow));
                    File.Move(staged, receipt, overwrite: true);
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
            catch (Exception ex) when (LibraryFiles.IsAccessError(ex) || ex is JsonException or ArgumentException or ExportException)
            { Log.Failure(DiagnosticCategory.General, "Could not track shared copy retention", ex); }
        }
    }

    // Called on the UI dispatcher: don't invalidate files currently offered for paste.
    internal static void CleanupExpired()
    {
        if (!Directory.Exists(RetentionJournal)) return;
        try
        {
            uint sequence = GetClipboardSequenceNumber();
            var clipboard = Clipboard.GetDataObject();
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (clipboard?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                if (clipboard.GetData(DataFormats.FileDrop) is not string[] paths) return;
                foreach (var path in paths) protectedPaths.Add(Path.GetFullPath(path));
            }
            else if (clipboard?.GetDataPresent("Shell IDList Array") == true) return;
            if (IsLastCopyCurrent && LastCopied != null) protectedPaths.Add(LastCopied);
            lock (ShareStorage)
            {
                MediaProcessing.RequireSafeDirectory(RetentionJournal);
                MediaProcessing.RequireSafeDirectory(Folder);
                foreach (string receipt in Directory.EnumerateFiles(RetentionJournal, "*.json").Take(128))
                {
                    if (GetClipboardSequenceNumber() != sequence) return;
                    try
                    {
                        if (LibraryFiles.InspectPath(receipt, directory: false) != ClipAvailability.Available ||
                            new FileInfo(receipt).Length > 16_384) continue;
                        var entry = JsonSerializer.Deserialize<ShareReceipt>(File.ReadAllText(receipt));
                        if (entry == null || !OwnedSharePath(Folder, entry.Path, Path.GetFileNameWithoutExtension(receipt)) ||
                            entry.Stamp.Identity == null || protectedPaths.Contains(entry.Path) ||
                            !RetentionExpired(entry.UsedAt, DateTimeOffset.UtcNow)) continue;
                        if (LibraryFiles.InspectPath(entry.Path, directory: false) != ClipAvailability.Available) continue;
                        using var lease = MediaOwnership.Current.TryMutate(entry.Path);
                        if (lease == null || MediaSource.Capture(entry.Path).Stamp != entry.Stamp) continue;
                        if (GetClipboardSequenceNumber() != sequence) return;
                        File.Delete(entry.Path);
                        File.Delete(receipt);
                        // Never recursively delete: preserve anything else a user put in the directory.
                        try { Directory.Delete(Path.GetDirectoryName(entry.Path)!); }
                        catch (IOException) { }
                    }
                    catch (Exception ex) when (LibraryFiles.IsAccessError(ex) || ex is JsonException or ArgumentException or ExportException)
                    { Log.Failure(DiagnosticCategory.General, "Could not expire a shared copy", ex); }
                }
            }
        }
        catch (Exception ex) when (LibraryFiles.IsAccessError(ex) || ex is ExternalException or ArgumentException or ExportException)
        { Log.Failure(DiagnosticCategory.General, "Shared copy cleanup deferred", ex); }
    }

    private static void ReserveShareFolder(string directory)
    {
        lock (ShareStorage)
        {
            MediaProcessing.RequireSafeDirectory(Folder);
            if (Directory.Exists(Folder) && Directory.EnumerateDirectories(Folder, "share-*").Take(128).Count() >= 128)
                throw new ExportException("Shared copies are retained for pasting. Free space in the shared copies folder before preparing more.");
            Directory.CreateDirectory(directory);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
