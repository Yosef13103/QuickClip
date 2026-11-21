using QuickClip.Discovery;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed partial class LibraryIndex
{
    // Catalog supplies positively established distinct titles, not arbitrary
    // store aliases or ambiguous shared executables. This never assigns a clip.
    internal Task<int> EnsureDiscoveredTitleFoldersAsync(IEnumerable<CatalogEntry> catalog,
        CancellationToken token = default)
    {
        var candidates = catalog.Where(game => !game.IsUnverified &&
                game.Rules.Inclusion != CatalogInclusion.Exclude &&
                game.Rules.Classification is AppClassification.Automatic or AppClassification.Game &&
                GameRecordingPolicy.IsId(game.Id, 256))
            .SelectMany(game => game.RelatedTitles.Where(title =>
                    title.Id != game.Id && game.AliasIds.Contains(title.Id) &&
                    GameRecordingPolicy.IsId(title.Id, 256) && ClipDetails.IsValidTag(title.Name))
                .Select(title => (Game: game.Id, Title: title.Id, Name: title.Name)))
            .DistinctBy(item => (item.Game, item.Title)).ToArray();
        if (candidates.Length == 0) return Task.FromResult(0);
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var tx = connection.BeginTransaction(deferred: false);
            int created = 0;
            foreach (var item in candidates)
            {
                ct.ThrowIfCancellationRequested();
                // Existing links (even deleted-folder tombstones) win over discovery names.
                if (Convert.ToInt64(Scalar(connection, tx, """
                    SELECT count(*) FROM discovered_title_folders WHERE game_id=$game AND title_id=$title;
                    """, ("$game", item.Game), ("$title", item.Title))) != 0) continue;
                var proposed = new LibraryCollection(Guid.NewGuid(), item.Name, item.Game, null, null).Normalize();
                string key = LibraryFiles.TextKey(proposed.Name);
                // Adopt a preexisting folder with exactly this normalized name and scope.
                // Do not touch its color, path preference or membership.
                string? id = Scalar(connection, tx, """
                    SELECT id FROM collections WHERE game_id=$game AND name_key=$key;
                    """, ("$game", item.Game), ("$key", key)) as string;
                if (id == null)
                {
                    id = Id(proposed.Id);
                    Execute(connection, tx, """
                        INSERT INTO collections(id,name,name_key,game_id) VALUES($id,$name,$key,$game);
                        """, ("$id", id), ("$name", proposed.Name), ("$key", key), ("$game", item.Game));
                    created++;
                }
                Execute(connection, tx, """
                    INSERT INTO discovered_title_folders(game_id,title_id,collection_id) VALUES($game,$title,$id);
                    """, ("$game", item.Game), ("$title", item.Title), ("$id", id));
            }
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return created;
        });
    }
}
