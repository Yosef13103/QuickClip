using Microsoft.Win32.SafeHandles;
using QuickClip.Discovery;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    // Explicit batch, all-or-nothing. Game association is organization metadata
    // only, and may fill a missing identity but never replace a captured identity.
    internal Task<int> AssignCollectionsAsync(IReadOnlyCollection<ClipAnnotationTarget> selection,
        Guid? collectionId, string? associateGameId, CancellationToken token = default)
    {
        var targets = selection.Take(257).ToArray();
        if (targets.Length is < 1 or > 256 || collectionId == Guid.Empty ||
            associateGameId != null && !GameRecordingPolicy.IsId(associateGameId, 256) ||
            targets.Any(t => t.Details.IndexId is null || t.Details.IndexId == Guid.Empty ||
                t.Details.IndexRevision < 0 || t.Source?.Identity == null) ||
            targets.Select(t => t.Details.IndexId).Distinct().Count() != targets.Length)
            throw new ArgumentException("Select up to 256 distinct verified recordings.");
        return Run(token, ct =>
        {
            var leases = new List<SafeFileHandle>();
            try
            {
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Path.IsPathFullyQualified(target.Path) || ClipLibrary.IsMomentsPath(target.Path) ||
                        !LibraryFiles.EligibleFile(target.Path) ||
                        LibraryFiles.InspectPath(target.Path, false) != ClipAvailability.Available)
                        throw new LibraryConflictException("A recording is unavailable. Refresh the Gallery.");
                    var lease = LibraryFiles.OpenMedia(target.Path);
                    leases.Add(lease);
                    var stamp = LibraryFiles.ReadStamp(lease);
                    if (stamp != target.Source || stamp.Bytes != target.Stamp.Length ||
                        stamp.Created != target.Stamp.Created.Ticks || stamp.Modified != target.Stamp.Modified.Ticks)
                        throw new LibraryConflictException("A recording changed. Refresh the Gallery.");
                }

                using var connection = _database.Open();
                using var tx = connection.BeginTransaction(deferred: false);
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    string id = Id(target.Details.IndexId!.Value);
                    var stamp = target.Source!.Value;
                    if (Convert.ToInt64(Scalar(connection, tx, """
                        SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id
                        WHERE c.id=$id AND c.revision=$revision AND c.path_key=$path
                          AND c.availability=0 AND r.availability=0 AND c.file_id=$identity
                          AND c.created_ticks=$created AND c.modified_ticks=$modified AND c.bytes=$bytes;
                        """, ("$id", id), ("$revision", target.Details.IndexRevision),
                        ("$path", LibraryFiles.PathKey(target.Path)), ("$identity", stamp.Identity),
                        ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes))) != 1)
                        throw new LibraryConflictException("A recording changed. Refresh the Gallery.");

                    if (associateGameId != null)
                    {
                        if (Convert.ToInt64(Scalar(connection, tx, """
                            SELECT count(*) FROM clips WHERE id=$id AND (game_id IS NULL OR game_id=$game);
                            """, ("$id", id), ("$game", associateGameId))) != 1)
                            throw new LibraryConflictException("Cannot replace an existing game identity.");
                        Execute(connection, tx, "UPDATE clips SET game_id=$game WHERE id=$id AND game_id IS NULL;",
                            ("$id", id), ("$game", associateGameId));
                    }
                    if (collectionId is Guid selected)
                    {
                        if (Convert.ToInt64(Scalar(connection, tx, """
                            SELECT count(*) FROM collections d JOIN clips c ON c.id=$clip
                            WHERE d.id=$collection AND (d.game_id='' OR d.game_id=c.game_id);
                            """, ("$clip", id), ("$collection", Id(selected)))) != 1)
                            throw new LibraryConflictException("The collection is not available for every selected recording.");
                        Execute(connection, tx, """
                            INSERT INTO clip_collections(clip_id,collection_id) VALUES($clip,$collection)
                            ON CONFLICT(clip_id) DO UPDATE SET collection_id=excluded.collection_id;
                            """, ("$clip", id), ("$collection", Id(selected)));
                    }
                    else Execute(connection, tx, "DELETE FROM clip_collections WHERE clip_id=$clip;", ("$clip", id));
                    Execute(connection, tx, "UPDATE clips SET revision=revision+1 WHERE id=$id;", ("$id", id));
                }
                ct.ThrowIfCancellationRequested();
                tx.Commit();
                return targets.Length;
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
        });
    }
}
