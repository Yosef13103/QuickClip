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
    internal event Action<IReadOnlyList<string>>? Releasing;

    internal ClipDeletion(Func<string, bool> recycle, Func<string, DateTime, RecycledFile?> find, Action<RecycledFile, string> restore) =>
        (_recycle, _find, _restore) = (recycle, find, restore);

    internal async Task<Result> DeleteAsync(IEnumerable<string> paths, Func<IReadOnlyList<string>, bool> confirm,
        Action? release = null, IProgress<int>? progress = null)
    {
        if (Busy) return new(0, 0, true);
        Busy = true;
        try
        {
            var batch = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (batch.Count == 0 || !confirm(batch)) return new(0, 0, true);
            Releasing?.Invoke(batch);
            release?.Invoke();
            var receipts = new List<RecycledFile>();
            var result = await RunOnStaAsync(() =>
            {
                int deleted = 0, failed = 0;
                foreach (string path in batch)
                {
                    DateTime since = DateTime.UtcNow.AddSeconds(-1);
                    bool sent = false;
                    if (!ClipLibrary.IsMomentsPath(path) && File.Exists(path))
                    {
                        for (int attempt = 0; attempt < 6; attempt++)
                        {
                            if (sent = _recycle(path)) break;
                            if (attempt < 5) Thread.Sleep(150);
                        }
                    }
                    if (sent)
                    {
                        deleted++;
                        try { if (_find(path, since) is { } receipt) receipts.Add(receipt); }
                        catch (Exception ex) { Log.Write("Recycle Bin undo receipt unavailable: " + ex.GetType().Name); }
                    }
                    else failed++;
                    progress?.Report(deleted + failed);
                }
                return new Result(deleted, failed);
            });
            if (result.Deleted > 0) { _undo.Clear(); _undo.AddRange(receipts); }
            return result;
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    internal async Task<Result> UndoAsync()
    {
        if (Busy) return new(0, 0, true);
        Busy = true;
        try
        {
            var receipts = _undo.ToArray();
            var restored = await Task.Run(() =>
            {
                var successes = new List<RecycledFile>();
                foreach (var receipt in receipts)
                {
                    if (Path.Exists(receipt.OriginalPath) || ClipLibrary.IsMomentsPath(receipt.OriginalPath)) continue;
                    try { _restore(receipt, receipt.OriginalPath); successes.Add(receipt); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
                return successes;
            });
            foreach (var receipt in restored) _undo.Remove(receipt);
            return new(restored.Count, receipts.Length - restored.Count);
        }
        finally { Busy = false; Changed?.Invoke(); }
    }

    private static Task<Result> RunOnStaAsync(Func<Result> work)
    {
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
