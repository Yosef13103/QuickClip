using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Tests;

public sealed class LibraryCollectionTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("qc-collections-");
        internal string Db => Path.Combine(_scratch.FullName, "library.sqlite3");
        internal string Root { get; }
        internal string Details { get; }
        internal string First { get; }
        internal string Second { get; }
        internal string Legacy { get; }
        internal LibraryIndex Index { get; }
        internal Fixture()
        {
            Root = _scratch.CreateSubdirectory("clips").FullName;
            Details = _scratch.CreateSubdirectory("details").FullName;
            First = Path.Combine(Root, "a.mp4");
            Second = Path.Combine(Root, "b.mp4");
            Legacy = Path.Combine(Root, "legacy.mp4");
            foreach (string path in new[] { First, Second, Legacy }) File.WriteAllText(path, "fixture");
            WriteMetadata(First, "steam:220", "Half-Life 2");
            WriteMetadata(Second, "steam:400", "Portal");
            Index = new(Db, Details, _ => new ClipHeader(10, null));
        }

        internal async Task InitializeAsync()
        {
            var rootId = await Index.RegisterRootAsync(Root);
            await Index.ReconcileAsync(rootId);
        }

        private void WriteMetadata(string path, string gameId, string game)
        {
            var file = new FileInfo(path);
            var details = new ClipDetails(DateTimeOffset.UtcNow, game, "Preserve title")
            {
                GameId = gameId, IsFavorite = true, Tags = ["Funny", "Multiplayer"],
                Bookmarks = [new QuickClip.Gallery.ClipBookmark(2, "Marker")],
            };
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
            File.WriteAllText(Path.Combine(Details, key + ".json"), JsonSerializer.Serialize(new
            {
                CreatedTicks = file.CreationTimeUtc.Ticks, ModifiedTicks = file.LastWriteTimeUtc.Ticks,
                Length = file.Length, Details = details,
            }));
        }

        internal async Task<ClipAnnotationTarget> TargetAsync(string path)
        {
            var row = Assert.Single(await Index.ReadGalleryAsync(null, path));
            using var handle = LibraryFiles.OpenMedia(path);
            return new(path, row.Stamp, row.Details) { Source = LibraryFiles.ReadStamp(handle) };
        }

        public void Dispose() => _scratch.Delete(recursive: true);
    }

    [Fact]
    public async Task Collections_are_scoped_exclusive_and_leave_ordinary_annotations_and_media_unchanged()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var one = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode 1", "steam:220", "#FB923C", "Episode 1"));
        var two = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode 2", "steam:220", null, null));
        var global = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Friends", null, null, null));
        var before = await f.TargetAsync(f.First);
        Assert.Equal("steam:220", before.Details.GameId);
        Assert.Equal("steam:400", (await f.TargetAsync(f.Second)).Details.GameId);
        Assert.Null((await f.TargetAsync(f.Legacy)).Details.GameId);
        Assert.True(global.AppliesTo(null));
        Assert.False(one.AppliesTo("steam:400"));
        Assert.False(one.AppliesTo(null));

        await f.Index.AssignCollectionAsync(before, one.Id);
        var assigned = await f.TargetAsync(f.First);
        Assert.Equal(one, assigned.Details.Collection);
        Assert.Equal(before.Source, assigned.Source);
        Assert.Equal(before.Details.Tags, assigned.Details.Tags);
        Assert.Equal(before.Details.IsFavorite, assigned.Details.IsFavorite);
        Assert.Equal(before.Details.Title, assigned.Details.Title);
        Assert.Equal(before.Details.Bookmarks, assigned.Details.Bookmarks);
        Assert.Equal(before.Details.IndexId, assigned.Details.IndexId);
        Assert.Equal(before.Details.IndexRevision + 1, assigned.Details.IndexRevision);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionAsync(before, two.Id));

        var second = await f.TargetAsync(f.Second);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionAsync(second, one.Id));
        Assert.Equal(second.Details.IndexRevision, (await f.TargetAsync(f.Second)).Details.IndexRevision);
        var legacy = await f.TargetAsync(f.Legacy);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionAsync(legacy, one.Id));
        await f.Index.AssignCollectionAsync(legacy, global.Id);
        Assert.Equal(global, (await f.TargetAsync(f.Legacy)).Details.Collection);

        await f.Index.AssignCollectionAsync(assigned, two.Id);
        var reassigned = await f.TargetAsync(f.First);
        Assert.Equal(two, reassigned.Details.Collection);
        var reopened = new LibraryIndex(f.Db);
        Assert.Equal(two, Assert.Single(await reopened.ReadGalleryAsync(null, f.First)).Details.Collection);
        Assert.Equal(3, (await reopened.ReadCollectionsAsync()).Length);
        await reopened.AssignCollectionAsync(reassigned, null);
        Assert.Null((await f.TargetAsync(f.First)).Details.Collection);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "Episode 1")));
        Assert.Equal("fixture", File.ReadAllText(f.First));
    }

    [Fact]
    public async Task Duplicate_names_and_overlapping_physical_destinations_are_rejected()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), " Episode 1 ", "steam:220", null, "Episode 1"));
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "EPISODE 1", "steam:220", null, null)));
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Global", null, null, "episode 1")));
        await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode 1", "steam:400", null, "Episode 1"));
        foreach (string invalid in new[] { "..", "CON.txt", @"A\B", "C:", "SteelSeries Moments", "Cache", "Trailing." })
            Assert.Throws<ArgumentException>(() => { _ = f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Bad", null, null, invalid)); });
        Assert.Equal(2, (await f.Index.ReadCollectionsAsync()).Length);
    }

    [Fact]
    public async Task E1_migration_keeps_existing_annotations_and_rejects_an_incompatible_v4()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var before = await f.TargetAsync(f.First);
        using (var connection = new LibraryDatabase(f.Db).Open())
            LibraryDatabase.Execute(connection, null, "DROP TABLE clip_collections; DROP TABLE collections; PRAGMA user_version=4;");
        var newer = new LibraryIndex(f.Db);
        var after = Assert.Single(await newer.ReadGalleryAsync(null, f.First));
        Assert.Equal(before.Details.IndexId, after.Details.IndexId);
        Assert.Equal(before.Details.Tags, after.Details.Tags);
        Assert.Null(after.Details.Collection);
        using (var connection = new LibraryDatabase(f.Db).Open())
            LibraryDatabase.Execute(connection, null, "DROP TABLE clip_collections; DROP TABLE collections; ALTER TABLE tags DROP COLUMN color; PRAGMA user_version=4;");
        await Assert.ThrowsAsync<InvalidDataException>(() => new LibraryIndex(f.Db).ReadCollectionsAsync());
        Assert.True(File.Exists(f.First));
    }
}
