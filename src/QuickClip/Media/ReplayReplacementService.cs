using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Shell;

namespace QuickClip.Media;

internal sealed class ReplayReplacementService(string directory, ReplayReplacementPolicy policy,
    Func<string, bool> recycle, Func<string, RecycledFile?> find, Func<string, bool> allowed,
    MediaOwnership ownership)
{
    private const int MaxJournals = 256;
    internal static ReplayReplacementService Current { get; } = new(
        Path.Combine(AppProfile.LocalDirectory, "replay-recovery"), ReplayReplacementPolicy.Current,
        RecycleBin.TrySend, path => RecycleBin.FindLatest(path, DateTime.MinValue),
        path => SavedRecordingFolders.ContainsMedia(AppSettings.Current, path), MediaOwnership.Current);

    internal string[] ReservedPaths(Guid id, IReadOnlyList<MediaSource> sources) =>
        [ReplayRecoveryJournal.JournalPath(directory, id),
            .. sources.Select((source, i) => ReplayRecoveryJournal.EscrowPath(source.Path, id, i))];

    // Called by the scheduler AFTER release of immutable read handles, while
    // its exclusive source/destination/editor reservations are still held.
    internal async Task<string> CommitAsync(Guid id, MediaSource output, IReadOnlyList<MediaSource> sources,
        IReadOnlyList<ClipDetails> details, bool automatic, CancellationToken token, Func<bool>? automaticAllowed = null)
    {
        token.ThrowIfCancellationRequested();
        if (automaticAllowed?.Invoke() == false || !allowed(output.Path) || sources.Any(source => !allowed(source.Path)))
            throw new ExportException("The combined copy was saved; the current library configuration prevents replacing originals.");
        if (Records().Length >= MaxJournals)
            throw new ExportException("The combined copy was saved. Restore prior replay groups before replacing more originals.");
        var journal = ReplayRecoveryJournal.Prepare(directory, output, sources, id);
        try
        {
            policy.Stage(journal, details, automatic, token);
            if (automaticAllowed?.Invoke() == false || !allowed(output.Path) || sources.Any(source => !allowed(source.Path)))
                throw new ExportException("Library locations changed during replay replacement.");
            await OnSta(() => journal.RecycleEscrow(token, recycle, find));
            return output.Path;
        }
        catch (Exception failure)
        {
            try
            {
                // Cancellation must not cancel the compensating restore. If a
                // collision/volume problem blocks it the journal remains recoverable.
                await OnSta(() => journal.Restore(CancellationToken.None, find));
                journal.RetireIfRestored();
            }
            catch (Exception rollback)
            {
                Log.Failure(DiagnosticCategory.Library, "Replay rollback requires manual recovery", rollback);
                throw new ExportException("The combined copy was saved, but restoring every original needs attention. Use Restore replay originals in Gallery.");
            }
            if (failure is OperationCanceledException) throw;
            Log.Failure(DiagnosticCategory.Library, "Replay replacement declined or rolled back", failure);
            throw new ExportException("The combined copy was saved and originals were kept. " +
                (failure is ExportException ? failure.Message : "Could not safely replace the originals."));
        }
    }

    internal ReplayRecoveryJournal[] List()
    {
        var pending = new List<ReplayRecoveryJournal>();
        foreach (var id in Records())
        {
            var journal = ReplayRecoveryJournal.Open(directory, id);
            if (journal.Items.Any(item => journal.Inspect(item) != ReplayOriginalState.Original))
                pending.Add(journal);
        }
        return pending.ToArray();
    }

    internal async Task RestoreAsync(Guid id, CancellationToken token)
    {
        var journal = ReplayRecoveryJournal.Open(directory, id);
        if (journal.Items.Any(item => !allowed(item.Source.Path)))
            throw new ExportException("Re-add the original library locations before restoring this group.");
        using var lease = ownership.TryAcquire([], journal.Items.Select(item => item.Source.Path)
            .Concat(ReservedPaths(id, journal.Items.Select(item => item.Source).ToArray())), protectEditorSources: true) ??
            throw new ExportException("A source is open in the editor or reserved by another media operation.");
        await OnSta(() => journal.Restore(token, find));
        journal.RetireIfRestored();
    }

    private Guid[] Records()
    {
        var status = LibraryFiles.InspectPath(directory, true);
        if (status == ClipAvailability.Missing) return [];
        if (status != ClipAvailability.Available)
            throw new ExportException("Replay recovery records are unavailable.");
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Select(path => Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Take(MaxJournals + 1).ToArray();
    }

    private static Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}