using QuickClip.Gallery;
using QuickClip.Media;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

/// <summary>Fence indexed annotations/drafts while staging. A caller already owns
/// exclusive media reservations, including editor protection, before entering.
/// No shell calls or lengthy remuxing under the SQLite write lock.</summary>
internal sealed class ReplayReplacementPolicy(LibraryDatabase database,
    Func<string, bool> inConfiguredLibrary, Func<FileStamp, bool> hasRecovery)
{
    internal static ReplayReplacementPolicy Current { get; } = new(
        new(Path.Combine(AppProfile.LocalDirectory, "library", "clips.sqlite3")),
        path => PersonalLibrary.Enabled && SavedRecordingFolders.ContainsMedia(AppSettings.Current, path),
        EditorRecovery.Exists);

    internal void Stage(ReplayRecoveryJournal journal, IReadOnlyList<ClipDetails> details,
        bool automatic, CancellationToken token)
    {
        if (details.Count != journal.Items.Count)
            throw new ExportException("Refresh the metadata for every original before replacing it.");
        token.ThrowIfCancellationRequested();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        for (int i = 0; i < journal.Items.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var source = journal.Items[i].Source;
            var expected = details[i];
            if (expected.IndexId == null || !inConfiguredLibrary(source.Path) ||
                !LibraryFiles.EligibleFile(source.Path) || hasRecovery(source.Stamp))
                throw new ExportException("An original is not in the current indexed library or has unsaved editor recovery.");
            source.Verify();
            // Do not trust a cached Gallery annotation snapshot. A draft may have
            // been written since selection without changing the media stamp.
            using var query = Command(connection, transaction, """
                SELECT c.favorite,c.revision,c.original_id,
                    EXISTS(SELECT 1 FROM clips child WHERE child.original_id=c.id),
                    EXISTS(SELECT 1 FROM edit_drafts d WHERE d.file_id=c.file_id AND
                        d.created_ticks=c.created_ticks AND d.modified_ticks=c.modified_ticks AND d.bytes=c.bytes)
                FROM clips c JOIN roots r ON r.id=c.root_id
                WHERE c.id=$clip AND c.path_key=$path AND c.availability=0 AND r.availability=0
                    AND c.created_ticks=$created AND c.modified_ticks=$modified AND c.bytes=$bytes AND c.file_id=$identity;
                """, ("$clip", expected.IndexId.Value.ToString("N")), ("$path", LibraryFiles.PathKey(source.Path)),
                ("$created", source.Stamp.Created), ("$modified", source.Stamp.Modified),
                ("$bytes", source.Stamp.Bytes), ("$identity", source.Stamp.Identity));
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(1) != expected.IndexRevision ||
                reader.GetBoolean(0) != expected.IsFavorite)
                throw new ExportException("An original or its metadata changed. Refresh the Gallery and choose again.");
            if (!reader.IsDBNull(2) || reader.GetBoolean(3) || reader.GetBoolean(4))
                throw new ExportException("An original has an edit draft or linked derivative. Keep it separately.");
            // Revisions greater than zero include explicit annotation edits.
            // New recording/combined legacy metadata starts at revision zero.
            // Manual replacement is explicitly confirmed and carries favorites/tags/bookmarks.
            if (automatic && (reader.GetBoolean(0) || reader.GetInt64(1) != 0))
                throw new ExportException("Automatic consolidation protects favorites and edited clip details.");
        }
        token.ThrowIfCancellationRequested();
        journal.Stage(token);
        // File identity moves have their own durable journal. This DB lock is
        // only a fence, never the authority for filesystem transaction state.
        transaction.Commit();
    }
}