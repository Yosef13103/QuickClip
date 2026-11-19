using QuickClip.Discovery;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

// One optional collection per recording, independent from its many ordinary
// tags. FolderLeaf is an opt-in destination preference, not a move command.
internal sealed record LibraryCollection(Guid Id, string Name, string? GameId,
    string? Color, string? FolderLeaf)
{
    internal bool AppliesTo(string? gameId) => GameId == null ||
        string.Equals(GameId, gameId, StringComparison.Ordinal);

    internal LibraryCollection Normalize()
    {
        string name = Name.Trim().Normalize();
        if (Id == Guid.Empty || !ClipDetails.IsValidTag(name) ||
            GameId != null && !GameRecordingPolicy.IsId(GameId, 256) ||
            !TagPalette.Valid(Color))
            throw new ArgumentException("Invalid collection identity, name, game or color.");

        string? leaf = FolderLeaf?.Normalize();
        if (leaf != null && (leaf.Length > 64 || leaf != leaf.Trim() ||
            !CatalogPaths.SafeLeaf(leaf) || leaf is "." or ".." ||
            leaf.IndexOfAny(['/', '\\', ':']) >= 0 ||
            new[] { "QuickClip", ProjectLibrary.FolderName, "Cache", "thumbs", "$Recycle.Bin",
                "System Volume Information", "SteelSeries Moments" }.Contains(leaf, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("Choose a safe, ordinary collection folder name.");
        return this with { Name = name, FolderLeaf = leaf };
    }
}

internal sealed partial class LibraryIndex
{
    internal Task<LibraryCollection[]> ReadCollectionsAsync(CancellationToken token = default) =>
        Run(token, ct =>
        {
            using var connection = _database.Open();
            using var query = Command(connection, null, """
                SELECT id,name,game_id,color,folder_leaf FROM collections
                ORDER BY game_id,name_key,id;
                """);
            using var reader = query.ExecuteReader();
            var result = new List<LibraryCollection>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    reader.GetString(2) is { Length: > 0 } scope ? scope : null,
                    ReadText(reader, 3), ReadText(reader, 4)));
            }
            return result.ToArray();
        });

    internal Task<LibraryCollection> CreateCollectionAsync(LibraryCollection definition,
        CancellationToken token = default)
    {
        var item = definition.Normalize();
        string scope = item.GameId ?? "";
        string key = LibraryFiles.TextKey(item.Name);
        string? folderKey = item.FolderLeaf == null ? null : LibraryFiles.TextKey(item.FolderLeaf);
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (Convert.ToInt64(Scalar(connection, transaction, """
                SELECT count(*) FROM collections WHERE id=$id OR (game_id=$scope AND name_key=$key);
                """, ("$id", Id(item.Id)), ("$scope", scope), ("$key", key))) != 0)
                throw new LibraryConflictException("A collection with that identity or name already exists in this scope.");
            if (folderKey != null && Convert.ToInt64(Scalar(connection, transaction, """
                SELECT count(*) FROM collections WHERE folder_key=$folder
                    AND (game_id=$scope OR game_id='' OR $scope='');
                """, ("$folder", folderKey), ("$scope", scope))) != 0)
                throw new LibraryConflictException("That folder name is already used by an overlapping collection.");
            Execute(connection, transaction, """
                INSERT INTO collections(id,name,name_key,game_id,color,folder_leaf,folder_key)
                VALUES($id,$name,$key,$scope,$color,$leaf,$folder);
                """, ("$id", Id(item.Id)), ("$name", item.Name), ("$key", key),
                ("$scope", scope), ("$color", item.Color), ("$leaf", item.FolderLeaf), ("$folder", folderKey));
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return item;
        });
    }

    // Metadata-only assignment, using the existing revision and exact media
    // identity fence. The caller must additionally enforce configured roots
    // and exclusions, as with Gallery tag edits.
    internal Task<long> AssignCollectionAsync(ClipAnnotationTarget target, Guid? collectionId,
        CancellationToken token = default)
    {
        if (target.Details.IndexId is null || target.Source?.Identity is null || collectionId == Guid.Empty ||
            !Path.IsPathFullyQualified(target.Path) || ClipLibrary.IsMomentsPath(target.Path) ||
            !LibraryFiles.EligibleFile(target.Path))
            throw new ArgumentException("Choose a verified indexed clip and a valid collection.");

        FileStamp stamp = default;
        return Mutate(target.Details.IndexId.Value, target.Details.IndexRevision, token, (connection, transaction, id) =>
        {
            if (stamp != target.Source || stamp.Bytes != target.Stamp.Length ||
                stamp.Created != target.Stamp.Created.Ticks || stamp.Modified != target.Stamp.Modified.Ticks ||
                Convert.ToInt64(Scalar(connection, transaction, """
                    SELECT count(*) FROM clips c JOIN roots r ON r.id=c.root_id
                    WHERE c.id=$id AND c.path_key=$path AND c.availability=0 AND r.availability=0
                        AND c.file_id=$identity AND c.created_ticks=$created AND c.modified_ticks=$modified AND c.bytes=$bytes;
                    """, ("$id", id), ("$path", LibraryFiles.PathKey(target.Path)), ("$identity", stamp.Identity),
                    ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes))) != 1)
                throw new LibraryConflictException("The recording changed. Refresh before assigning a collection.");

            if (collectionId is Guid selected)
            {
                // Never infer game identity from a filename or display label.
                if (Convert.ToInt64(Scalar(connection, transaction, """
                    SELECT count(*) FROM collections d JOIN clips c ON c.id=$clip
                    WHERE d.id=$collection AND (d.game_id='' OR d.game_id=c.game_id);
                    """, ("$clip", id), ("$collection", Id(selected)))) != 1)
                    throw new LibraryConflictException("This collection is unavailable for the recording's game identity.");
                Execute(connection, transaction, """
                    INSERT INTO clip_collections(clip_id,collection_id) VALUES($clip,$collection)
                    ON CONFLICT(clip_id) DO UPDATE SET collection_id=excluded.collection_id;
                    """, ("$clip", id), ("$collection", Id(selected)));
            }
            else Execute(connection, transaction, "DELETE FROM clip_collections WHERE clip_id=$clip;", ("$clip", id));
        }, leaseSource: () =>
        {
            if (LibraryFiles.InspectPath(target.Path, false) != ClipAvailability.Available)
                throw new LibraryConflictException("The clip is unavailable.");
            var handle = LibraryFiles.OpenMedia(target.Path);
            try
            {
                stamp = LibraryFiles.ReadStamp(handle);
                return handle;
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        });
    }
}
