using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    // Convert a definition and its existing assignments in one metadata-only
    // transaction. Never silently replace an existing folder.
    internal Task<int> ConvertTagToCollectionAsync(LibraryTagDefinition expected, CancellationToken token = default)
    {
        var item = new LibraryCollection(Guid.NewGuid(), expected.Name, expected.GameId, expected.Color, null).Normalize();
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            VerifyTag(connection, tx, expected);
            if (Convert.ToInt64(Scalar(connection, tx,
                "SELECT count(*) FROM collections WHERE game_id=$scope AND name_key=$key;",
                ("$scope", item.GameId ?? ""), ("$key", LibraryFiles.TextKey(item.Name)))) != 0)
                throw new LibraryConflictException("A folder with this name already exists.");
            if (Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM clip_tags t JOIN clip_collections c ON c.clip_id=t.clip_id WHERE t.tag_id=$tag;
                """, ("$tag", expected.Id))) != 0)
                throw new LibraryConflictException("Some clips already have a folder. Remove those folder assignments first.");
            if (item.GameId != null && Convert.ToInt64(Scalar(connection, tx, """
                SELECT count(*) FROM clip_tags t JOIN clips c ON c.id=t.clip_id WHERE t.tag_id=$tag AND c.game_id IS NOT $scope;
                """, ("$tag", expected.Id), ("$scope", item.GameId))) != 0)
                throw new LibraryConflictException("Some clips belong to another game.");
            Execute(connection, tx, """
                INSERT INTO collections(id,name,name_key,game_id,color) VALUES($id,$name,$key,$scope,$color);
                """, ("$id", Id(item.Id)), ("$name", item.Name), ("$key", LibraryFiles.TextKey(item.Name)),
                ("$scope", item.GameId ?? ""), ("$color", item.Color));
            int count = Execute(connection, tx, """
                INSERT INTO clip_collections(clip_id,collection_id) SELECT clip_id,$id FROM clip_tags WHERE tag_id=$tag;
                """, ("$id", Id(item.Id)), ("$tag", expected.Id));
            Execute(connection, tx, "UPDATE clips SET revision=revision+1 WHERE id IN (SELECT clip_id FROM clip_tags WHERE tag_id=$tag);", ("$tag", expected.Id));
            Execute(connection, tx, "DELETE FROM clip_tags WHERE tag_id=$tag;", ("$tag", expected.Id));
            Execute(connection, tx, "DELETE FROM tags WHERE id=$tag;", ("$tag", expected.Id));
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return count;
        });
    }
}
