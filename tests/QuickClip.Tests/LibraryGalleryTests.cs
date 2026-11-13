using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Tests;

public sealed class LibraryGalleryTests
{
    [Theory]
    [InlineData(120)]
    [InlineData(1200)]
    public async Task Synthetic_library_reuses_headers_after_incremental_refresh(int count)
    {
        var scratch = Directory.CreateTempSubdirectory("qc-large-gallery-");
        try
        {
            string root = scratch.CreateSubdirectory("clips").FullName;
            string[] folders = Enumerable.Range(0, 10)
                .Select(i => Directory.CreateDirectory(Path.Combine(root, $"Game {i}")).FullName).ToArray();
            for (int i = 0; i < count; i++)
                File.WriteAllText(Path.Combine(folders[i % folders.Length], $"clip-{i:D4}.mp4"), "fixture");

            int probes = 0;
            var index = new LibraryIndex(Path.Combine(scratch.FullName, "library.sqlite3"), null,
                _ => { Interlocked.Increment(ref probes); return new ClipHeader(4, null); });
            var id = await index.RegisterRootAsync(root);
            await index.ReconcileAsync(id);
            Assert.Equal(count, (await index.ReadGalleryAsync(id, null)).Count);
            Assert.Equal(count, probes);

            File.WriteAllText(Path.Combine(folders[0], "new.mp4"), "new fixture");
            await index.ReconcileAsync(id);
            Assert.Equal(count + 1, (await index.ReadGalleryAsync(id, null)).Count);
            Assert.Equal(count + 1, probes);
        }
        finally { scratch.Delete(recursive: true); }
    }

    [Fact]
    public async Task Imports_legacy_annotations_and_saves_one_revision_without_changing_media()
    {
        var scratch = Directory.CreateTempSubdirectory("quickclip-gallery-index-");
        try
        {
            string root = scratch.CreateSubdirectory("clips").FullName;
            string path = Path.Combine(root, "example.mp4");
            await File.WriteAllTextAsync(path, "synthetic header fixture");
            string legacy = scratch.CreateSubdirectory("legacy").FullName;
            var file = new FileInfo(path);
            var details = new ClipDetails(DateTimeOffset.UtcNow, "Fixture", "Original title")
            {
                IsFavorite = true, Tags = ["Practice"],
                Bookmarks = [new QuickClip.Gallery.ClipBookmark(2, "Live") { SessionId = Guid.NewGuid() }],
            };
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            await File.WriteAllTextAsync(Path.Combine(legacy, key + ".json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                CreatedTicks = file.CreationTimeUtc.Ticks, ModifiedTicks = file.LastWriteTimeUtc.Ticks, Length = file.Length, Details = details,
            }));
            string database = Path.Combine(scratch.FullName, "library.sqlite3");
            var index = new LibraryIndex(database, legacy, _ => new ClipHeader(4, null));
            var id = await index.RegisterRootAsync(root);
            await index.ReconcileAsync(id);
            var row = Assert.Single(await index.ReadGalleryAsync(id, null));
            Assert.True(row.Details.IsFavorite);
            Assert.Equal("Practice", Assert.Single(row.Details.Tags));
            var bookmark = Assert.Single(row.Details.Bookmarks);
            Assert.NotNull(bookmark.Id);
            Assert.Equal(details.Bookmarks[0].SessionId, bookmark.SessionId);
            var target = new ClipAnnotationTarget(path, row.Stamp, row.Details);
            var draft = row.Details with { Title = "Edited", Tags = ["Ranked"], Bookmarks = [bookmark with { Label = "Renamed" }] };
            Assert.Equal(1, await index.SaveGalleryDetailsAsync(target, draft, CancellationToken.None));
            await Assert.ThrowsAsync<LibraryConflictException>(() =>
                index.SaveGalleryDetailsAsync(target, details, CancellationToken.None));

            var reopened = Assert.Single(await new LibraryIndex(database).ReadGalleryAsync(id, null));
            Assert.Equal("Edited", reopened.Details.Title);
            Assert.Equal("Ranked", Assert.Single(reopened.Details.Tags));
            Assert.Equal(bookmark.Id, Assert.Single(reopened.Details.Bookmarks).Id);
            Assert.Equal("Renamed", reopened.Details.Bookmarks[0].Label);
            Assert.Equal("synthetic header fixture", await File.ReadAllTextAsync(path));
        }
        finally { scratch.Delete(recursive: true); }
    }
}
