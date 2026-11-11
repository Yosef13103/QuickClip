using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    public Task<ClipAnnotations> GetAnnotationsAsync(Guid clipId, CancellationToken cancellationToken = default) =>
        Run(cancellationToken, token =>
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            long revision;
            bool favorite;
            using (var command = Command(connection, transaction,
                "SELECT revision,favorite FROM clips WHERE id=$id;", ("$id", Id(clipId))))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read()) throw new KeyNotFoundException("Clip was not found in the index.");
                revision = reader.GetInt64(0);
                favorite = reader.GetBoolean(1);
            }

            var tags = ImmutableArray.CreateBuilder<string>();
            using (var command = Command(connection, transaction, """
                SELECT t.name FROM clip_tags ct JOIN tags t ON t.id=ct.tag_id WHERE ct.clip_id=$id ORDER BY t.name_key;
                """, ("$id", Id(clipId))))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) tags.Add(reader.GetString(0));

            var bookmarks = ImmutableArray.CreateBuilder<ClipBookmark>();
            using (var command = Command(connection, transaction, """
                SELECT id,offset_ticks,label,origin,session_id,captured_ticks,captured_offset
                FROM bookmarks WHERE clip_id=$id ORDER BY offset_ticks,id;
                """, ("$id", Id(clipId))))
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    bookmarks.Add(new(Guid.Parse(reader.GetString(0)), TimeSpan.FromTicks(reader.GetInt64(1)), ReadText(reader, 2),
                        (BookmarkOrigin)reader.GetInt32(3), ReadGuid(reader, 4), reader.IsDBNull(5) ? null : ReadDate(reader, 5, 6)));
                }
            transaction.Commit();
            return new ClipAnnotations(revision, favorite, tags.ToImmutable(), bookmarks.ToImmutable());
        });

    public Task<long> SetFavoriteAsync(Guid clipId, bool favorite, long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
            Execute(connection, transaction, "UPDATE clips SET favorite=$value WHERE id=$id;",
                ("$value", favorite ? 1 : 0), ("$id", id)));

    public Task<long> SetTagsAsync(Guid clipId, IEnumerable<string> tags, long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
        {
            ArgumentNullException.ThrowIfNull(tags);
            var names = tags.Take(65).Select(name => RequiredText(name, 64, nameof(tags))).ToArray();
            if (names.Length > 64) throw new ArgumentException("Use at most 64 tags.", nameof(tags));
            Execute(connection, transaction, "DELETE FROM clip_tags WHERE clip_id=$id;", ("$id", id));
            foreach (string name in names.DistinctBy(LibraryFiles.TextKey))
            {
                string key = LibraryFiles.TextKey(name);
                Execute(connection, transaction, """
                    INSERT INTO tags(name,name_key) VALUES($name,$key) ON CONFLICT(name_key) DO NOTHING;
                    INSERT INTO clip_tags(clip_id,tag_id) SELECT $id,id FROM tags WHERE name_key=$key;
                    """, ("$name", name), ("$key", key), ("$id", id));
            }
        });

    /// <summary>Null or blank title and game reset their respective path-derived defaults.</summary>
    public Task<long> SetClipDetailsAsync(Guid clipId, string? title, string? game, string? gameId = null,
        long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
        {
            string path, root;
            using (var command = Command(connection, transaction, """
                SELECT c.path,r.path FROM clips c JOIN roots r ON r.id=c.root_id WHERE c.id=$id;
                """, ("$id", id)))
            using (var reader = command.ExecuteReader())
            {
                reader.Read();
                path = reader.GetString(0);
                root = reader.GetString(1);
            }
            string? titleOverride = OptionalText(title, 256, nameof(title));
            string? gameOverride = OptionalText(game, 256, nameof(game));
            string displayTitle = titleOverride ?? Path.GetFileNameWithoutExtension(path);
            string displayGame = gameOverride ?? ClipLibrary.InitialGame(root, path);
            Execute(connection, transaction, """
                UPDATE clips SET title=$title,title_key=$titleKey,title_edited=$titleEdited,
                    game=$game,game_key=$gameKey,game_edited=$gameEdited,game_id=$gameId WHERE id=$id;
                """, ("$title", displayTitle), ("$titleKey", LibraryFiles.TextKey(displayTitle)), ("$titleEdited", titleOverride != null ? 1 : 0),
                ("$game", displayGame), ("$gameKey", LibraryFiles.TextKey(displayGame)), ("$gameEdited", gameOverride != null ? 1 : 0),
                ("$gameId", OptionalText(gameId, 256, nameof(gameId))), ("$id", id));
        });

    public Task<long> UpsertBookmarkAsync(Guid clipId, ClipBookmark bookmark, long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
        {
            ArgumentNullException.ThrowIfNull(bookmark);
            if (bookmark.Id == Guid.Empty || bookmark.SessionId == Guid.Empty || !Enum.IsDefined(bookmark.Origin))
                throw new ArgumentException("Invalid bookmark identity or origin.", nameof(bookmark));
            if (Scalar(connection, transaction, "SELECT duration_ticks FROM clips WHERE id=$id;", ("$id", id)) is not long duration)
                throw new InvalidOperationException("A verified clip duration is required to save a bookmark.");
            if (bookmark.Offset.Ticks < 0 || bookmark.Offset.Ticks >= duration)
                throw new ArgumentOutOfRangeException(nameof(bookmark), "The bookmark must fall within the clip.");
            int updated = Execute(connection, transaction, """
                INSERT INTO bookmarks(id,clip_id,offset_ticks,label,origin,session_id,captured_ticks,captured_offset)
                VALUES($bookmark,$id,$ticks,$label,$origin,$session,$captured,$offset)
                ON CONFLICT(id) DO UPDATE SET offset_ticks=excluded.offset_ticks,label=excluded.label,origin=excluded.origin,
                    session_id=excluded.session_id,captured_ticks=excluded.captured_ticks,captured_offset=excluded.captured_offset
                WHERE bookmarks.clip_id=excluded.clip_id;
                """, ("$bookmark", Id(bookmark.Id)), ("$id", id), ("$ticks", bookmark.Offset.Ticks),
                ("$label", OptionalText(bookmark.Label, 128, nameof(bookmark))), ("$origin", (int)bookmark.Origin),
                ("$session", bookmark.SessionId?.ToString("N")), ("$captured", bookmark.CapturedAt?.UtcTicks),
                ("$offset", (int?)bookmark.CapturedAt?.Offset.TotalMinutes));
            if (updated != 1) throw new LibraryConflictException("The bookmark belongs to another clip.");
        });

    public Task<long> DeleteBookmarkAsync(Guid clipId, Guid bookmarkId, long? expectedRevision = null,
        CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
        {
            if (Execute(connection, transaction, "DELETE FROM bookmarks WHERE id=$bookmark AND clip_id=$id;",
                ("$bookmark", Id(bookmarkId)), ("$id", id)) != 1)
                throw new KeyNotFoundException("Bookmark was not found for this clip.");
        });

    public Task<long> SetClipAssociationAsync(Guid clipId, Guid? originalId, Guid? sessionId,
        long? expectedRevision = null, CancellationToken cancellationToken = default) =>
        Mutate(clipId, expectedRevision, cancellationToken, (connection, transaction, id) =>
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Invalid session identity.", nameof(sessionId));
            if (originalId != null && Convert.ToInt64(Scalar(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM clips c JOIN roots cr ON cr.id=c.root_id
                    JOIN clips o ON o.id=$original JOIN roots rr ON rr.id=o.root_id
                    WHERE c.id=$id AND cr.category=$export AND rr.category=$source);
                """, ("$original", Id(originalId.Value)), ("$id", id), ("$export", (int)MediaCategory.Export),
                ("$source", (int)MediaCategory.QuickClipOriginal))) == 0)
                throw new ArgumentException("An export may reference an indexed QuickClip original.", nameof(originalId));
            Execute(connection, transaction, "UPDATE clips SET original_id=$original,session_id=$session WHERE id=$id;",
                ("$original", originalId?.ToString("N")), ("$session", sessionId?.ToString("N")), ("$id", id));
        });

    private Task<long> Mutate(Guid clipId, long? expectedRevision, CancellationToken cancellationToken,
        Action<SqliteConnection, SqliteTransaction, string> edit) =>
        Run(cancellationToken, token =>
        {
            if (clipId == Guid.Empty || expectedRevision < 0) throw new ArgumentException("Invalid clip identity or revision.");
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();
            string id = Id(clipId);
            var value = Scalar(connection, transaction, """
                UPDATE clips SET revision=revision+1 WHERE id=$id AND ($expected IS NULL OR revision=$expected)
                RETURNING revision;
                """, ("$id", id), ("$expected", expectedRevision));
            if (value is not long revision)
            {
                if (Scalar(connection, transaction, "SELECT revision FROM clips WHERE id=$id;", ("$id", id)) is null)
                    throw new KeyNotFoundException("Clip was not found in the index.");
                throw new LibraryConflictException("The clip metadata changed. Refresh before saving.");
            }
            edit(connection, transaction, id);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return revision;
        });

    private static string? OptionalText(string? value, int limit, string argument) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredText(value, limit, argument);

    private static string RequiredText(string? value, int limit, string argument)
    {
        string text = value?.Trim() ?? "";
        if (text.Length == 0 || text.Length > limit || text.Any(char.IsControl))
            throw new ArgumentException("Invalid text length or control character.", argument);
        return text.Normalize();
    }
}
