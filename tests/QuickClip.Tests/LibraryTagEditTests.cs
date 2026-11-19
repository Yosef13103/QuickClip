using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Tests;

public sealed class LibraryTagEditTests
{
    [Fact]
    public async Task Tag_only_write_preserves_original_defaults_favorite_and_bookmarks()
    {
        var scratch = Directory.CreateTempSubdirectory("qc-tag-edit-");
        try
        {
            string root = scratch.CreateSubdirectory("clips").FullName;
            string path = Path.Combine(root, "example.mp4");
            File.WriteAllText(path, "synthetic fixture");
            string database = Path.Combine(scratch.FullName, "library.sqlite3");
            var index = new LibraryIndex(database, null, _ => new ClipHeader(4, null));
            var rootId = await index.RegisterRootAsync(root);
            await index.ReconcileAsync(rootId);
            var row = Assert.Single(await index.ReadGalleryAsync(rootId, null));
            var id = row.Details.IndexId!.Value;
            await index.SetTagsAsync(id, ["Funny", "Keep"]);
            await index.SetFavoriteAsync(id, true);
            await index.UpsertBookmarkAsync(id, new QuickClip.Library.ClipBookmark(Guid.NewGuid(),
                TimeSpan.FromSeconds(1), "Marked", BookmarkOrigin.Editor));
            row = Assert.Single(await index.ReadGalleryAsync(rootId, null));
            var original = row.Details;
            var target = new ClipAnnotationTarget(path, row.Stamp, original)
            {
                Source = new FileStamp(row.Stamp.Created.Ticks, row.Stamp.Modified.Ticks, row.Stamp.Length, row.Identity),
            };
            var replacement = new ClipTagEdit("Funny", "Ranked").Apply(original);
            // Intentionally include unrelated changes; a tag-only edit must not save them.
            long revision = await index.SaveGalleryDetailsAsync(target,
                replacement with { Title = "Wrong", Game = "Wrong", IsFavorite = false, Bookmarks = [] },
                default, tagsOnly: true);
            Assert.Equal(original.IndexRevision + 1, revision);
            var saved = Assert.Single(await index.ReadGalleryAsync(rootId, null)).Details;
            Assert.Equal(original.IndexId, saved.IndexId);
            Assert.Equal(original.Game, saved.Game);
            Assert.Equal(original.Title, saved.Title);
            Assert.Equal(original.IsFavorite, saved.IsFavorite);
            Assert.Equal(original.Bookmarks, saved.Bookmarks);
            Assert.Equal(new[] { "Keep", "Ranked" }, saved.Tags);
            using (var connection = new LibraryDatabase(database).Open())
            {
                Assert.Equal(0L, Convert.ToInt64(LibraryDatabase.Scalar(connection, null,
                    "SELECT title_edited+game_edited FROM clips WHERE id=$id;", ("$id", id.ToString("N")))));
            }
            await Assert.ThrowsAsync<LibraryConflictException>(() =>
                index.SaveGalleryDetailsAsync(target, replacement, default, tagsOnly: true));
            Assert.Equal("synthetic fixture", File.ReadAllText(path));
        }
        finally { scratch.Delete(recursive: true); }
    }
}
