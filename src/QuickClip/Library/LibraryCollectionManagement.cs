using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    internal Task<int> UpdateCollectionAsync(LibraryCollection expected, LibraryCollection replacement,
        CancellationToken token = default)
    {
        var next = replacement.Normalize();
        if (expected.Id != next.Id)
            throw new ArgumentException("Collection identity cannot change.");
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            VerifyCollection(connection, tx, expected);
            if (Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM collections WHERE id<>$id AND
                  ((game_id=$scope AND name_key=$key) OR
                   (folder_key=$folder AND (game_id=$scope OR game_id='' OR $scope='')));
                """, ("$id", Id(next.Id)), ("$scope", next.GameId ?? ""),
                ("$key", LibraryFiles.TextKey(next.Name)),
                ("$folder", next.FolderLeaf == null ? null : LibraryFiles.TextKey(next.FolderLeaf)))) != 0)
                throw new LibraryConflictException("The name or folder overlaps another collection.");
            if (next.GameId != null && Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM clip_collections cc JOIN clips c ON c.id=cc.clip_id
                WHERE cc.collection_id=$id AND c.game_id IS NOT $scope;
                """, ("$id", Id(next.Id)), ("$scope", next.GameId))) != 0)
                throw new LibraryConflictException("Remove incompatible memberships before changing scope.");
            Execute(connection, tx, """
                UPDATE collections SET name=$name,name_key=$key,game_id=$scope,color=$color,
                  folder_leaf=$leaf,folder_key=$folder WHERE id=$id;
                """, ("$id", Id(next.Id)), ("$name", next.Name), ("$key", LibraryFiles.TextKey(next.Name)),
                ("$scope", next.GameId ?? ""), ("$color", next.Color), ("$leaf", next.FolderLeaf),
                ("$folder", next.FolderLeaf == null ? null : LibraryFiles.TextKey(next.FolderLeaf)));
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return 1;
        });
    }

    // Explicit definition deletion clears memberships, including offline clips,
    // but does not touch media, ordinary annotations or other collections.
    internal Task<int> DeleteCollectionAsync(LibraryCollection expected, CancellationToken token = default) =>
        Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            VerifyCollection(connection, tx, expected);
            Execute(connection, tx, """
                UPDATE clips SET revision=revision+1 WHERE id IN
                    (SELECT clip_id FROM clip_collections WHERE collection_id=$id);
                """, ("$id", Id(expected.Id)));
            int count = Execute(connection, tx, "DELETE FROM clip_collections WHERE collection_id=$id;",
                ("$id", Id(expected.Id)));
            Execute(connection, tx, "DELETE FROM collections WHERE id=$id;", ("$id", Id(expected.Id)));
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return count;
        });

    private static void VerifyCollection(Microsoft.Data.Sqlite.SqliteConnection connection,
        Microsoft.Data.Sqlite.SqliteTransaction tx, LibraryCollection expected)
    {
        if (Convert.ToInt64(Scalar(connection, tx, """
            SELECT count(*) FROM collections WHERE id=$id AND name=$name AND game_id=$scope
                AND color IS $color AND folder_leaf IS $leaf;
            """, ("$id", Id(expected.Id)), ("$name", expected.Name), ("$scope", expected.GameId ?? ""),
            ("$color", expected.Color), ("$leaf", expected.FolderLeaf))) != 1)
            throw new LibraryConflictException("Collection changed. Reopen management before editing.");
    }
}
