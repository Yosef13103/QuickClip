using Microsoft.Data.Sqlite;
using QuickClip.Discovery;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    // Capture sidecar is a hint, not authority to create a collection. Deletion,
    // scope changes and stale choices safely resolve to no membership.
    private static void AssignImportedCollection(SqliteConnection connection, SqliteTransaction tx,
        string clipId, Guid collectionId)
    {
        Execute(connection, tx, """
            INSERT INTO clip_collections(clip_id,collection_id)
            SELECT c.id,d.id FROM clips c JOIN collections d ON d.id=$collection
            WHERE c.id=$clip AND c.game_id IS NOT NULL AND (d.game_id='' OR d.game_id=c.game_id)
            ON CONFLICT(clip_id) DO NOTHING;
            """, ("$clip", clipId), ("$collection", Id(collectionId)));
    }

    // Exact path repair only for a fresh save callback. A filesystem watcher
    // may have indexed the published file before its sidecar became visible.
    // Never infer identity or reapply a selection to an edited/imported clip.
    internal Task<bool> RepairRecordedCollectionAsync(string path, CancellationToken token = default) =>
        Run(token, ct =>
        {
            if (!Path.IsPathFullyQualified(path) || ClipLibrary.IsMomentsPath(path) ||
                !LibraryFiles.EligibleFile(path) || LibraryFiles.InspectPath(path, false) != ClipAvailability.Available)
                return false;
            using var lease = LibraryFiles.OpenMedia(path);
            var stamp = LibraryFiles.ReadStamp(lease);
            if (stamp.Identity == null) return false;
            var details = LegacyLibraryMetadata.Read(_legacyDetails, path, stamp).Details;
            if (details == null || !GameRecordingPolicy.IsId(details.GameId, 256)) return false;
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var id = Scalar(connection, tx, """
                SELECT c.id FROM clips c JOIN roots r ON c.root_id=r.id
                WHERE c.path_key=$path AND c.file_id=$identity AND c.created_ticks=$created
                  AND c.modified_ticks=$modified AND c.bytes=$bytes AND c.availability=0 AND r.availability=0
                  AND c.revision=0 AND c.game_id IS NULL AND c.legacy_metadata<>$imported
                  AND NOT EXISTS(SELECT 1 FROM clip_collections cc WHERE cc.clip_id=c.id);
                """, ("$path", LibraryFiles.PathKey(path)), ("$identity", stamp.Identity),
                ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes),
                ("$imported", (int)LegacyMetadataStatus.Imported)) as string;
            if (id == null) return false;
            Execute(connection, tx, "UPDATE clips SET game_id=$game,revision=revision+1 WHERE id=$id;",
                ("$game", details.GameId), ("$id", id));
            if (details.RecordedCollectionId is Guid selected && selected != Guid.Empty)
                AssignImportedCollection(connection, tx, id, selected);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return true;
        });
}
