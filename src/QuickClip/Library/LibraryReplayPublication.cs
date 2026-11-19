using QuickClip.Gallery;
using QuickClip.Media;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    // Register a particular freshly saved/proven replay. Never traverse its
    // folder, mark neighbors missing, or claim that a full scan happened.
    internal Task<bool> RegisterPublishedReplayAsync(Guid rootId, MediaSource source, CancellationToken token) =>
        Run(token, ct =>
        {
            using var media = source.OpenVerified();
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            LibraryRoot root;
            string? priorVolume;
            using (var query = Command(connection, transaction,
                "SELECT id,path,category,exclusions,availability,scanned_ticks,volume FROM roots WHERE id=$root;",
                ("$root", Id(rootId))))
            using (var row = query.ExecuteReader())
            {
                if (!row.Read()) return false;
                root = ReadRoot(row);
                priorVolume = row.IsDBNull(6) ? null : row.GetString(6);
            }
            if (!LibraryFiles.Within(source.Path, root.Directory) ||
                LibraryFiles.IsExcluded(root.Directory, source.Path, root.ExcludedDirectories) ||
                !LibraryFiles.EligibleFile(source.Path) ||
                LibraryFiles.InspectPath(root.Directory, true) != ClipAvailability.Available ||
                LibraryFiles.ShouldSkip(File.GetAttributes(media)) ||
                new DriveInfo(Path.GetPathRoot(root.Directory)!).DriveType is not (DriveType.Fixed or DriveType.Removable))
                return false;
            string? volume = LibraryFiles.RootVolume(root.Directory);
            if (volume == null || priorVolume != null && volume != priorVolume || source.Stamp.Identity == null)
                return false;

            string key = LibraryFiles.PathKey(source.Path);
            Existing? prior = null;
            long revision = 0;
            LegacyMetadataStatus legacyStatus = LegacyMetadataStatus.None;
            using (var query = Command(connection, transaction, """
                SELECT id,path,path_key,created_ticks,modified_ticks,bytes,file_id,availability,revision,legacy_metadata
                FROM clips WHERE root_id=$root AND path_key=$key;
                """, ("$root", Id(rootId)), ("$key", key)))
            using (var row = query.ExecuteReader())
            {
                if (row.Read())
                {
                    prior = new(row.GetString(0), row.GetString(1), row.GetString(2),
                        new(row.GetInt64(3), row.GetInt64(4), row.GetInt64(5), row.IsDBNull(6) ? null : row.GetString(6)),
                        (ClipAvailability)row.GetInt32(7));
                    revision = row.GetInt64(8);
                    legacyStatus = (LegacyMetadataStatus)row.GetInt32(9);
                }
            }

            // If a Gallery scan won the publication race, it may have indexed
            // the media before its sidecar was committed. Repair only untouched
            // revision-zero metadata, never replace a user's changed annotations.
            var legacy = LegacyLibraryMetadata.Read(_legacyDetails, source.Path, source.Stamp);
            if (legacy.Status != LegacyMetadataStatus.Imported || legacy.Details == null) return false;
            if (prior != null && !prior.Stamp.SameOccupant(source.Stamp))
            {
                Execute(connection, transaction, "UPDATE clips SET path_key=NULL,availability=1 WHERE id=$id;", ("$id", prior.Id));
                prior = null;
            }
            if (prior != null && (prior.Stamp != source.Stamp || revision != 0)) return false;
            if (prior == null)
            {
                var header = _readHeader(source.Path);
                ApplyObservation(connection, transaction, root, key, new(source.Path, source.Stamp, header, legacy), null);
            }
            else
            {
                ApplyObservation(connection, transaction, root, key, new(source.Path, source.Stamp, null, null), prior);
                if (legacyStatus != LegacyMetadataStatus.Imported)
                {
                    var details = legacy.Details.Normalize();
                    string title = details.Title ?? Path.GetFileNameWithoutExtension(source.Path);
                    Execute(connection, transaction, """
                        UPDATE clips SET captured_ticks=$captured,captured_offset=$offset,date_source=$date,
                            legacy_metadata=$legacy,title=$title,title_key=$titleKey,title_edited=$titleEdited,
                            game=$game,game_key=$gameKey,game_edited=1,revision=revision+1 WHERE id=$id AND revision=0;
                        """, ("$captured", details.CapturedAt.UtcTicks), ("$offset", (int)details.CapturedAt.Offset.TotalMinutes),
                        ("$date", (int)CaptureDateSource.LegacyDetails), ("$legacy", (int)LegacyMetadataStatus.Imported),
                        ("$title", title), ("$titleKey", LibraryFiles.TextKey(title)), ("$titleEdited", details.Title != null ? 1 : 0),
                        ("$game", details.Game), ("$gameKey", LibraryFiles.TextKey(details.Game)), ("$id", prior.Id));
                    var ticks = Scalar(connection, transaction, "SELECT duration_ticks FROM clips WHERE id=$id;", ("$id", prior.Id));
                    WriteGalleryAnnotations(connection, transaction, prior.Id, details, ticks as long?);
                }
            }
            Execute(connection, transaction, """
                UPDATE roots SET availability=0,volume=coalesce(volume,$volume),scan_revision=scan_revision+1 WHERE id=$root;
                """, ("$volume", volume), ("$root", Id(rootId)));
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        });
}