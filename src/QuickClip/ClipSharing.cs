using System.Collections.Specialized;
using System.Runtime.InteropServices;
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
                        return await MediaProcessing.PublishAsync(output, ".mp4", null, async staging =>
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
                await TryCopyAsync(file, () => ticket == _lastClipboardRequest && GetClipboardSequenceNumber() == sequence);
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
        return await TryCopyAsync(path, () => ticket == _lastClipboardRequest && GetClipboardSequenceNumber() == sequence);
    }

    private static async Task<bool> TryCopyAsync(string path, Func<bool> stillRequested)
    {
        if (!stillRequested() || !File.Exists(path)) return false;
        try
        {
            if (!ShellClipboard.TryCopyFile(path, stillRequested)) return false;
            LastCopied = path;
            _lastCopySequence = GetClipboardSequenceNumber();
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
                return true;
            }
            catch (ExternalException) { await Task.Delay(100); }
        }
        return false;
    }

    private static readonly object ShareStorage = new();

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
