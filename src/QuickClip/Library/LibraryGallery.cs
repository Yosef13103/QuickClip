using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    internal sealed record GalleryRow(string Path, ClipMediaStamp Stamp, ClipDetails Details, double? Duration);
    private sealed record BookmarkRow(string Id, long Ticks, string? Label, string? Session);

    internal Task<List<GalleryRow>> ReadGalleryAsync(Guid? root, string? path, CancellationToken token = default) =>
        Run(token, cancellation =>
        {
            using var connection = _database.Open();
            using var command = Command(connection, null, """
                SELECT c.id,c.path,c.bytes,c.modified_ticks,c.created_ticks,c.captured_ticks,c.captured_offset,c.game,
                    CASE WHEN c.title_edited=1 THEN c.title END,c.favorite,c.revision,c.duration_ticks,
                    (SELECT json_group_array(name) FROM
                        (SELECT t.name FROM clip_tags ct JOIN tags t ON t.id=ct.tag_id WHERE ct.clip_id=c.id ORDER BY t.name_key)),
                    (SELECT json_group_array(json_object('Id',id,'Ticks',offset_ticks,'Label',label,'Session',session_id))
                        FROM bookmarks WHERE clip_id=c.id)
                FROM clips c JOIN roots r ON r.id=c.root_id
                WHERE r.availability=0 AND c.availability=0
                    AND ($root IS NULL OR c.root_id=$root) AND ($path IS NULL OR c.path_key=$path)
                ORDER BY c.captured_ticks DESC,c.id;
                """, ("$root", root?.ToString("N")), ("$path", path == null ? null : LibraryFiles.PathKey(path)));
            using var reader = command.ExecuteReader();
            var rows = new List<GalleryRow>();
            while (reader.Read())
            {
                cancellation.ThrowIfCancellationRequested();
                var details = new ClipDetails(ReadDate(reader, 5, 6), reader.GetString(7), ReadText(reader, 8))
                {
                    IndexId = Guid.Parse(reader.GetString(0)),
                    IndexRevision = reader.GetInt64(10),
                    IsFavorite = reader.GetBoolean(9),
                    Tags = JsonSerializer.Deserialize<string[]>(reader.GetString(12)) ?? [],
                    Bookmarks = (JsonSerializer.Deserialize<BookmarkRow[]>(reader.GetString(13)) ?? [])
                        .Select(mark => new Gallery.ClipBookmark(TimeSpan.FromTicks(mark.Ticks).TotalSeconds, mark.Label ?? "Marker")
                        { Id = Guid.Parse(mark.Id), SessionId = mark.Session == null ? null : Guid.Parse(mark.Session) }).OrderBy(mark => mark.Seconds).ToArray(),
                };
                rows.Add(new(reader.GetString(1), new(reader.GetInt64(2),
                    new DateTime(reader.GetInt64(3), DateTimeKind.Utc), new DateTime(reader.GetInt64(4), DateTimeKind.Utc)),
                    details, reader.IsDBNull(11) ? null : TimeSpan.FromTicks(reader.GetInt64(11)).TotalSeconds));
            }
            return rows;
        });

    internal Task<long> SaveGalleryDetailsAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token) =>
        Mutate(target.Details.IndexId ?? Guid.Empty, target.Details.IndexRevision, token, (connection, transaction, id) =>
        {
            // Check both the indexed path and current file identity before editing a cached card or draft.
            using var handle = LibraryFiles.OpenMedia(target.Path);
            var stamp = LibraryFiles.ReadStamp(handle);
            if (stamp.Bytes != target.Stamp.Length || stamp.Created != target.Stamp.Created.Ticks ||
                stamp.Modified != target.Stamp.Modified.Ticks ||
                Convert.ToInt64(Scalar(connection, transaction, """
                    SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id
                    WHERE c.id=$id AND c.path_key=$path AND c.availability=0 AND r.availability=0
                        AND c.created_ticks=$created AND c.modified_ticks=$modified AND c.bytes=$bytes AND c.file_id IS $identity;
                    """, ("$id", id), ("$path", LibraryFiles.PathKey(target.Path)), ("$created", stamp.Created),
                    ("$modified", stamp.Modified), ("$bytes", stamp.Bytes), ("$identity", stamp.Identity))) != 1)
                throw new LibraryConflictException("The media changed. Reopen it before saving details.");

            string? titleOverride = OptionalText(draft.Title, 256, nameof(draft.Title));
            string title = titleOverride ?? Path.GetFileNameWithoutExtension(target.Path);
            string game = RequiredText(draft.Game, 256, nameof(draft.Game));
            Execute(connection, transaction, """
                UPDATE clips SET title=$title,title_key=$titleKey,title_edited=$titleEdited,
                    game=$game,game_key=$gameKey,game_edited=1 WHERE id=$id;
                """, ("$title", title), ("$titleKey", LibraryFiles.TextKey(title)), ("$titleEdited", titleOverride == null ? 0 : 1),
                ("$game", game), ("$gameKey", LibraryFiles.TextKey(game)), ("$id", id));
            var ticks = Scalar(connection, transaction, "SELECT duration_ticks FROM clips WHERE id=$id;", ("$id", id));
            WriteGalleryAnnotations(connection, transaction, id, draft.Normalize(), ticks as long?, preserveExisting: true);
        });

    private static void WriteGalleryAnnotations(SqliteConnection connection, SqliteTransaction transaction,
        string id, ClipDetails details, long? durationTicks, bool preserveExisting = false)
    {
        Execute(connection, transaction, """
            UPDATE clips SET favorite=$favorite WHERE id=$id;
            DELETE FROM clip_tags WHERE clip_id=$id;
            """, ("$id", id), ("$favorite", details.IsFavorite ? 1 : 0));
        foreach (string name in details.Tags)
            Execute(connection, transaction, """
                INSERT INTO tags(name,name_key) VALUES($name,$key) ON CONFLICT(name_key) DO NOTHING;
                INSERT INTO clip_tags(clip_id,tag_id) SELECT $id,id FROM tags WHERE name_key=$key;
                """, ("$name", name), ("$key", LibraryFiles.TextKey(name)), ("$id", id));

        var retained = new HashSet<string>();
        foreach (var mark in details.Bookmarks)
        {
            long offset = TimeSpan.FromSeconds(mark.Seconds).Ticks;
            if (durationTicks is long bound && offset >= bound)
            {
                if (preserveExisting) throw new ArgumentException("The bookmark must fall within the clip.");
                continue;
            }
            // Newly copied/exported legacy metadata belongs to a new clip. Its bookmarks get new row IDs.
            string markerId = Id(preserveExisting ? mark.Id ?? Guid.NewGuid() : Guid.NewGuid());
            retained.Add(markerId);
            if (Execute(connection, transaction, """
                INSERT INTO bookmarks(id,clip_id,offset_ticks,label,origin,session_id)
                VALUES($marker,$id,$offset,$label,$origin,$session)
                ON CONFLICT(id) DO UPDATE SET offset_ticks=excluded.offset_ticks,label=excluded.label
                WHERE clip_id=excluded.clip_id;
                """, ("$marker", markerId), ("$id", id), ("$offset", offset), ("$label", mark.Label),
                ("$origin", mark.SessionId.HasValue ? (int)BookmarkOrigin.Live : (int)BookmarkOrigin.Editor),
                ("$session", mark.SessionId?.ToString("N"))) != 1)
                throw new LibraryConflictException("A bookmark belongs to another clip.");
        }

        if (!preserveExisting) return;
        using var command = Command(connection, transaction, "SELECT id FROM bookmarks WHERE clip_id=$id;", ("$id", id));
        var removed = new List<string>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                if (!retained.Contains(reader.GetString(0))) removed.Add(reader.GetString(0));
        foreach (string markerId in removed)
            Execute(connection, transaction, "DELETE FROM bookmarks WHERE id=$marker AND clip_id=$id;",
                ("$marker", markerId), ("$id", id));
    }
}
