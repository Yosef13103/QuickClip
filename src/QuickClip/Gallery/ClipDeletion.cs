using QuickClip.Shell;

namespace QuickClip.Gallery;

/// <summary>One user-confirmed batch and its recovery receipts. Media is only removed through the shell.</summary>
internal sealed class ClipDeletion
{
    internal sealed record Result(int Deleted, int Failed, bool Cancelled = false);
    internal static ClipDeletion Current { get; } = new(RecycleBin.TrySend, RecycleBin.FindLatest, RecycleBin.Restore);
    private readonly Func<string, bool> _recycle;
    private readonly Func<string, DateTime, RecycledFile?> _find;
    private readonly Action<RecycledFile, string> _restore;
    private readonly List<RecycledFile> _undo = [];
    internal bool Busy { get; private set; }
    internal bool CanUndo => _undo.Count > 0;
    internal event Action? Changed;

    internal ClipDeletion(Func<string, bool> recycle, Func<string, DateTime, RecycledFile?> find, Action<RecycledFile, string> restore) =>
        (_recycle, _find, _restore) = (recycle, find, restore);

    internal async Task<Result> DeleteAsync(IEnumerable<string> paths, Func<IReadOnlyList<string>, bool> confirm, Action? release = null)
    {
        if (Busy) return new(0, 0, true);
        Busy = true;
        int deleted = 0, failed = 0;
        try
        {
            var batch = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (batch.Count == 0 || !confirm(batch)) return new(0, 0, true);
            release?.Invoke(); // only unload the player once the user confirms
            var receipts = new List<RecycledFile>();
            foreach (string path in batch)
            {
                DateTime since = DateTime.UtcNow.AddSeconds(-1);
                bool sent = false;
                // mpv may need a moment to release its file handle; don't hammer the shell on every retry.
                for (int attempt = 0; attempt < 6 && !(sent = _recycle(path)); attempt++) await Task.Delay(150);
                if (!sent) { failed++; continue; }
                deleted++;
                try { if (_find(path, since) is { } receipt) receipts.Add(receipt); }
                catch (Exception ex) { Log.Write("Recycle Bin undo receipt unavailable: " + ex.Message); }
            }
            if (deleted > 0) { _undo.Clear(); _undo.AddRange(receipts); }
            return new(deleted, failed);
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    internal Result Undo()
    {
        if (Busy) return new(0, 0, true);
        Busy = true;
        int restored = 0, failed = 0;
        try
        {
            foreach (var receipt in _undo.ToArray())
            {
                // A new recording at the old path must never be overwritten.
                if (Path.Exists(receipt.OriginalPath)) { failed++; continue; }
                try { _restore(receipt, receipt.OriginalPath); _undo.Remove(receipt); restored++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed++; }
            }
            return new(restored, failed);
        }
        finally { Busy = false; Changed?.Invoke(); }
    }
}
