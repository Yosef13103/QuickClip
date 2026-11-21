using Microsoft.Data.Sqlite;
using QuickClip.Discovery;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed record LibraryTagDefinition(long Id, string Name, string? GameId, string? Color)
{
    internal bool AppliesTo(string? gameId) => GameId == null || GameId == gameId;
    internal LibraryTagDefinition Normalize()
    {
        string name = Name.Trim().Normalize();
        if (!ClipDetails.IsValidTag(name) || GameId != null && !GameRecordingPolicy.IsId(GameId, 256) ||
            !TagPalette.Valid(Color))
            throw new ArgumentException("Invalid tag name, scope or color.");
        return this with { Name = name };
    }
}

internal sealed partial class LibraryIndex
{
    private static LibraryTagDefinition ReadTagDefinition(SqliteDataReader reader) =>
        new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2) is { Length: > 0 } game ? game : null,
            ReadText(reader, 3));

    internal Task<LibraryTagDefinition[]> ReadTagDefinitionsAsync(CancellationToken token = default) =>
        Run(token, ct =>
        {
            using var connection = _database.Open();
            using var query = Command(connection, null, "SELECT id,name,game_id,color FROM tags ORDER BY name_key,game_id,id;");
            using var reader = query.ExecuteReader();
            var result = new List<LibraryTagDefinition>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(ReadTagDefinition(reader));
            }
            return result.ToArray();
        });

    private static LibraryTagDefinition[] ReadAssignedTags(SqliteConnection connection, SqliteTransaction tx, string clipId)
    {
        using var query = Command(connection, tx, """
            SELECT t.id,t.name,t.game_id,t.color FROM clip_tags ct JOIN tags t ON t.id=ct.tag_id
            WHERE ct.clip_id=$id ORDER BY t.name_key,t.id;
            """, ("$id", clipId));
        using var reader = query.ExecuteReader();
        var result = new List<LibraryTagDefinition>();
        while (reader.Read()) result.Add(ReadTagDefinition(reader));
        return result.ToArray();
    }

    private static LibraryTagDefinition? FindTag(SqliteConnection connection, SqliteTransaction tx, string key, string? scope)
    {
        using var query = Command(connection, tx,
            "SELECT id,name,game_id,color FROM tags WHERE name_key=$key AND game_id=$scope;",
            ("$key", key), ("$scope", scope ?? ""));
        using var reader = query.ExecuteReader();
        return reader.Read() ? ReadTagDefinition(reader) : null;
    }

    private static string? ReadClipGameId(SqliteConnection connection, SqliteTransaction tx, string clipId) =>
        Scalar(connection, tx, "SELECT game_id FROM clips WHERE id=$id;", ("$id", clipId)) as string;

    // The legacy free-text editor retains an existing definition with that
    // name, otherwise prefers the applicable specific scope, then global.
    // An explicitly selected definition always uses its stable ID instead.
    private static LibraryTagDefinition ResolveTagForClip(SqliteConnection connection, SqliteTransaction tx,
        string clipId, string name, bool legacyImport = false, bool globalOnly = false)
    {
        string key = LibraryFiles.TextKey(name);
        string? gameId = ReadClipGameId(connection, tx, clipId);
        if (!globalOnly && !legacyImport)
        {
            var current = ReadAssignedTags(connection, tx, clipId)
                .FirstOrDefault(t => t.AppliesTo(gameId) && LibraryFiles.TextKey(t.Name) == key);
            if (current != null) return current;
            if (gameId != null && FindTag(connection, tx, key, gameId) is { } scoped) return scoped;
        }
        if (FindTag(connection, tx, key, null) is { } global) return global;
        if (!legacyImport && !globalOnly && Convert.ToInt64(Scalar(connection, tx,
            "SELECT count(*) FROM tags WHERE name_key=$key;", ("$key", key))) != 0)
            throw new LibraryConflictException("This tag is defined for another game. Create a global tag explicitly to use it here.");

        Execute(connection, tx, "INSERT INTO tags(name,name_key,game_id) VALUES($name,$key,'');",
            ("$name", name), ("$key", key));
        return FindTag(connection, tx, key, null)!;
    }

    private static void ReplaceAssignedTags(SqliteConnection connection, SqliteTransaction tx, string clipId,
        IEnumerable<string> names, bool legacyImport = false)
    {
        var tags = names.DistinctBy(LibraryFiles.TextKey).Select(name =>
            ResolveTagForClip(connection, tx, clipId, name, legacyImport)).ToArray();
        Execute(connection, tx, "DELETE FROM clip_tags WHERE clip_id=$id;", ("$id", clipId));
        foreach (var tag in tags)
            Execute(connection, tx, "INSERT INTO clip_tags(clip_id,tag_id) VALUES($clip,$tag);",
                ("$clip", clipId), ("$tag", tag.Id));
    }

    private static void VerifyTag(SqliteConnection connection, SqliteTransaction tx, LibraryTagDefinition expected)
    {
        if (expected.Id <= 0 || Convert.ToInt64(Scalar(connection, tx, """
            SELECT count(*) FROM tags WHERE id=$id AND name=$name AND game_id=$scope AND color IS $color;
            """, ("$id", expected.Id), ("$name", expected.Name), ("$scope", expected.GameId ?? ""),
            ("$color", expected.Color))) != 1)
            throw new LibraryConflictException("Tag definition changed. Refresh before editing.");
    }

    internal Task<LibraryTagDefinition> CreateTagDefinitionAsync(LibraryTagDefinition definition, CancellationToken token = default)
    {
        var item = definition.Normalize();
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            string key = LibraryFiles.TextKey(item.Name);
            if (FindTag(connection, tx, key, item.GameId) != null)
                throw new LibraryConflictException("A tag with this name already exists in the selected scope.");
            Execute(connection, tx, """
                INSERT INTO tags(name,name_key,game_id,color) VALUES($name,$key,$scope,$color);
                """, ("$name", item.Name), ("$key", key), ("$scope", item.GameId ?? ""), ("$color", item.Color));
            var result = FindTag(connection, tx, key, item.GameId)!;
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return result;
        });
    }

    internal Task<int> UpdateTagDefinitionAsync(LibraryTagDefinition expected, LibraryTagDefinition replacement,
        CancellationToken token = default)
    {
        var item = replacement.Normalize();
        if (item.Id != expected.Id) throw new ArgumentException("Tag identity cannot change.");
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            VerifyTag(connection, tx, expected);
            string key = LibraryFiles.TextKey(item.Name);
            if (FindTag(connection, tx, key, item.GameId) is { } conflict && conflict.Id != item.Id)
                throw new LibraryConflictException("A tag with this name already exists in that scope. Use replacement to merge tags.");
            if (item.GameId != null && Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM clip_tags ct JOIN clips c ON c.id=ct.clip_id
                WHERE ct.tag_id=$id AND c.game_id IS NOT $scope;
                """, ("$id", item.Id), ("$scope", item.GameId))) != 0)
                throw new LibraryConflictException("Remove incompatible tag assignments before changing scope.");
            if (Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM clip_tags ct JOIN clip_tags other ON other.clip_id=ct.clip_id
                JOIN tags t ON t.id=other.tag_id WHERE ct.tag_id=$id AND other.tag_id<>$id AND t.name_key=$key;
                """, ("$id", item.Id), ("$key", key))) != 0)
                throw new LibraryConflictException("Another tag of that name is already assigned to a recording. Replace it first.");
            Execute(connection, tx, """
                UPDATE tags SET name=$name,name_key=$key,game_id=$scope,color=$color WHERE id=$id;
                """, ("$name", item.Name), ("$key", key), ("$scope", item.GameId ?? ""), ("$color", item.Color), ("$id", item.Id));
            if (expected.Name != item.Name || expected.GameId != item.GameId)
                Execute(connection, tx, "UPDATE clips SET revision=revision+1 WHERE id IN (SELECT clip_id FROM clip_tags WHERE tag_id=$id);",
                    ("$id", item.Id));
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return 1;
        });
    }

    internal Task<int> DeleteTagDefinitionAsync(LibraryTagDefinition expected, CancellationToken token = default) =>
        Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            VerifyTag(connection, tx, expected);
            Execute(connection, tx, "UPDATE clips SET revision=revision+1 WHERE id IN (SELECT clip_id FROM clip_tags WHERE tag_id=$id);",
                ("$id", expected.Id));
            int count = Execute(connection, tx, "DELETE FROM clip_tags WHERE tag_id=$id;", ("$id", expected.Id));
            Execute(connection, tx, "DELETE FROM tags WHERE id=$id;", ("$id", expected.Id));
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return count;
        });
}
