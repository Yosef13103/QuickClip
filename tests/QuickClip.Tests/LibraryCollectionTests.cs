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

        internal void WriteMetadata(string path, string gameId, string game, Guid? collectionId = null, Guid? sessionId = null)
        {
            var file = new FileInfo(path);
            var details = new ClipDetails(DateTimeOffset.UtcNow, game, "Preserve title")
            {
                GameId = gameId, RecordedCollectionId = collectionId, RecordingSessionId = sessionId, IsFavorite = true, Tags = ["Funny", "Multiplayer"],
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
    public async Task Session_identity_round_trips_through_index_and_fresh_save_repair()
    {
        using var f = new Fixture();
        var session = Guid.NewGuid();
        f.WriteMetadata(f.First, "steam:220", "Half-Life 2", sessionId: session);
        await f.InitializeAsync();
        Assert.Equal(session, (await f.TargetAsync(f.First)).Details.RecordingSessionId);
        Assert.Null((await f.TargetAsync(f.Legacy)).Details.RecordingSessionId);
        f.WriteMetadata(f.Legacy, "steam:220", "Half-Life 2", sessionId: session);
        Assert.True(await f.Index.RepairRecordedCollectionAsync(f.Legacy));
        Assert.Equal(session, (await f.TargetAsync(f.Legacy)).Details.RecordingSessionId);
        Assert.False(await f.Index.RepairRecordedCollectionAsync(f.Legacy));
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
            LibraryDatabase.Execute(connection, null, "DROP TABLE clip_collections; DROP TABLE collections; CREATE TABLE tags_v4(id INTEGER PRIMARY KEY,name TEXT NOT NULL,name_key TEXT NOT NULL UNIQUE,color TEXT); INSERT INTO tags_v4 SELECT id,name,name_key,color FROM tags; CREATE TABLE clip_tags_v4(clip_id TEXT NOT NULL REFERENCES clips(id) ON DELETE CASCADE,tag_id INTEGER NOT NULL REFERENCES tags_v4(id),PRIMARY KEY(clip_id,tag_id)); INSERT INTO clip_tags_v4 SELECT * FROM clip_tags; DROP TABLE clip_tags; DROP TABLE tags; ALTER TABLE tags_v4 RENAME TO tags; ALTER TABLE clip_tags_v4 RENAME TO clip_tags; CREATE INDEX clip_tags_reverse ON clip_tags(tag_id,clip_id); PRAGMA user_version=4;");
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
    [Fact]
    public async Task Management_preserves_membership_on_edit_and_clears_only_membership_on_delete()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var original = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode 1", null, null, null));
        await f.Index.AssignCollectionAsync(await f.TargetAsync(f.First), original.Id);
        var before = await f.TargetAsync(f.First);
        var edited = original with { Name = "Episode One", GameId = "steam:220", Color = "#FB923C", FolderLeaf = "Episode One" };
        await f.Index.UpdateCollectionAsync(original, edited);
        Assert.Equal(edited, (await f.TargetAsync(f.First)).Details.Collection);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.UpdateCollectionAsync(edited, edited with { GameId = "steam:400" }));
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.DeleteCollectionAsync(original));
        Assert.Equal(1, await f.Index.DeleteCollectionAsync(edited));
        var after = await f.TargetAsync(f.First);
        Assert.Null(after.Details.Collection);
        Assert.Equal(before.Source, after.Source);
        Assert.Equal(before.Details.Tags, after.Details.Tags);
        Assert.Equal(before.Details.Bookmarks, after.Details.Bookmarks);
        Assert.Equal(before.Details.IndexId, after.Details.IndexId);
        Assert.Equal(before.Details.IndexRevision + 1, after.Details.IndexRevision);
        Assert.False(Directory.Exists(Path.Combine(f.Root, "Episode One")));
    }
    [Fact]
    public async Task Bulk_assignment_is_atomic_and_explicit_legacy_association_preserves_annotations()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var collection = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode", "steam:220", null, null));
        var first = await f.TargetAsync(f.First);
        var second = await f.TargetAsync(f.Second);
        var legacy = await f.TargetAsync(f.Legacy);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionsAsync([first, second], collection.Id, null));
        Assert.Equal(first.Details.IndexRevision, (await f.TargetAsync(f.First)).Details.IndexRevision);
        Assert.Null((await f.TargetAsync(f.First)).Details.Collection);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionsAsync([legacy], collection.Id, null));
        Assert.Equal(2, await f.Index.AssignCollectionsAsync([first, legacy], collection.Id, "steam:220"));
        var associated = await f.TargetAsync(f.Legacy);
        Assert.Equal("steam:220", associated.Details.GameId);
        Assert.Equal(collection, associated.Details.Collection);
        Assert.Equal(legacy.Details.IndexId, associated.Details.IndexId);
        Assert.Equal(legacy.Source, associated.Source);
        Assert.Equal(legacy.Details.Tags, associated.Details.Tags);
        await Assert.ThrowsAsync<LibraryConflictException>(() => f.Index.AssignCollectionsAsync([associated], null, "steam:400"));
        Assert.Equal(1, await f.Index.AssignCollectionsAsync([associated], null, null));
        Assert.Null((await f.TargetAsync(f.Legacy)).Details.Collection);
    }

    [Fact]
    public void Collection_navigation_uses_ids_not_names()
    {
        var id = Guid.NewGuid();
        var collection = new LibraryCollection(id, "Episode 1", "steam:220", null, null);
        var first = new ClipDetails(DateTimeOffset.UtcNow, "Same name") { GameId = "steam:220", Collection = collection };
        var unrelated = first with { GameId = "steam:400", Collection = null };
        var legacy = first with { GameId = null, Collection = null };
        var group = GalleryFolderKey.For(first);
        Assert.True(new GalleryFolderSelection(group).Matches(first));
        Assert.False(new GalleryFolderSelection(group).Matches(unrelated));
        Assert.False(new GalleryFolderSelection(group).Matches(legacy));
        Assert.True(new GalleryFolderSelection(group, true, id).Matches(first));
        Assert.False(new GalleryFolderSelection(group, true).Matches(first));
        Assert.True(new GalleryFolderSelection(group, true).Matches(first with { Collection = null }));
        Assert.True(new GalleryFolderSelection(GalleryFolderKey.All).Matches(legacy));
    }

    [Fact]
    public async Task Recorded_collection_import_and_late_repair_are_scope_checked_and_do_not_reapply_after_edits()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var collection = await f.Index.CreateCollectionAsync(new(Guid.NewGuid(), "Episode 1", "steam:220", null, null));
        f.WriteMetadata(f.Legacy, "steam:220", "Half-Life 2", collection.Id);
        Assert.True(await f.Index.RepairRecordedCollectionAsync(f.Legacy));
        var repaired = await f.TargetAsync(f.Legacy);
        Assert.Equal(collection, repaired.Details.Collection);
        await f.Index.AssignCollectionsAsync([repaired], null, null);
        Assert.False(await f.Index.RepairRecordedCollectionAsync(f.Legacy));
        Assert.Null((await f.TargetAsync(f.Legacy)).Details.Collection);

        string fresh = Path.Combine(f.Root, "new.mp4");
        File.WriteAllText(fresh, "new fixture");
        f.WriteMetadata(fresh, "steam:220", "Half-Life 2", collection.Id);
        string wrong = Path.Combine(f.Root, "other.mp4");
        File.WriteAllText(wrong, "other fixture");
        f.WriteMetadata(wrong, "steam:400", "Portal", collection.Id);
        await f.InitializeAsync();
        Assert.Equal(collection, (await f.TargetAsync(fresh)).Details.Collection);
        Assert.Null((await f.TargetAsync(wrong)).Details.Collection);
    }

    [Fact]
    public void Collection_preference_manual_none_automatic_exact_match_and_json_roundtrip()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var pref = new GameCollectionPreference
        {
            ManualCollection = first,
            Executables = System.Collections.Immutable.ImmutableDictionary<string,Guid>.Empty
                .Add(@"C:\Games\Test\game.exe", second)
        };
        var settings = new AppSettings { EnableLibraryIndex = true, RecordingCollections =
            System.Collections.Immutable.ImmutableDictionary<string,GameCollectionPreference>.Empty.Add("steam:42", pref) };
        Assert.Equal(first, RecordingCollections.Resolve(settings, "steam:42", @"C:\Games\Test\game.exe"));
        Assert.Null(RecordingCollections.Resolve(settings, null, @"C:\Games\Test\game.exe"));
        var loaded = AppSettings.Parse(settings.ToJson());
        Assert.Equal(first, RecordingCollections.Resolve(loaded, "steam:42", null));
        var automatic = pref with { Automatic = true };
        Assert.Equal(second, automatic.Resolve(@"c:\games\test\GAME.exe"));
        Assert.Null(automatic.Resolve(@"C:\Other\game.exe"));
        Assert.Null((pref with { ManualCollection = null }).Resolve(@"C:\Games\Test\game.exe"));
        Assert.Null((automatic with { Executables = automatic.Executables.Add(@"c:\games\test\GAME.exe", first) }).Resolve(@"C:\Games\Test\game.exe"));
    }

    [Fact]
    public async Task Scoped_tags_are_distinct_and_batch_assignment_is_atomic()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var hl = await f.Index.CreateTagDefinitionAsync(new(0, "Modded", "steam:220", "#FB923C"));
        var portal = await f.Index.CreateTagDefinitionAsync(new(0, "Modded", "steam:400", "#60A5FA"));
        var global = await f.Index.CreateTagDefinitionAsync(new(0, "Modded", null, null));
        Assert.NotEqual(hl.Id, portal.Id);
        Assert.False(hl.AppliesTo("steam:400"));
        Assert.True(global.AppliesTo(null));
        var a = await f.TargetAsync(f.First);
        var b = await f.TargetAsync(f.Second);
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.ApplyTagDeltaAsync([a,b], [], [], default, addDefinitions: [hl]));
        Assert.Equal(a.Details.IndexRevision, (await f.TargetAsync(f.First)).Details.IndexRevision);
        await f.Index.ApplyTagDeltaAsync([a], [], [], default, addDefinitions: [hl]);
        await f.Index.ApplyTagDeltaAsync([b], [], [], default, addDefinitions: [portal]);
        a = await f.TargetAsync(f.First);
        b = await f.TargetAsync(f.Second);
        Assert.Contains(hl, a.Details.TagDefinitions);
        Assert.Contains(portal, b.Details.TagDefinitions);
        Assert.True(new GalleryTagFilter(hl.Id, hl.Name).Matches(a.Details));
        Assert.False(new GalleryTagFilter(hl.Id, hl.Name).Matches(b.Details));
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.ApplyTagDeltaAsync([a], [], [], default, addDefinitions: [global]));
        await f.Index.SaveGalleryDetailsAsync(a, a.Details with { IsFavorite = false }, default);
        a = await f.TargetAsync(f.First);
        Assert.Contains(hl, a.Details.TagDefinitions);
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.UpdateTagDefinitionAsync(hl, hl with { GameId = "steam:400", Name = "Other" }));
        await f.Index.ApplyTagDeltaAsync([a], [], [], default, addDefinitions: [global], removeDefinitions: [hl]);
        Assert.Contains(global, (await f.TargetAsync(f.First)).Details.TagDefinitions);
        await f.Index.DeleteTagDefinitionAsync(portal);
        Assert.DoesNotContain(portal, (await f.TargetAsync(f.Second)).Details.TagDefinitions);
        var recreated = await f.Index.CreateTagDefinitionAsync(portal with { Id = 0 });
        Assert.NotEqual(portal.Id, recreated.Id);
        Assert.Equal("fixture", File.ReadAllText(f.First));
    }
}
