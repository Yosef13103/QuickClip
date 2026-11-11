using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Tests;

public sealed class LibraryIndexTests : IDisposable
{
    private static readonly DateTimeOffset CaptureTime = new(2025, 4, 5, 19, 23, 0, TimeSpan.FromHours(3));
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-library-");
    private string Db => Path.Combine(_fixture.FullName, "catalog.sqlite3");
    private string Media => Path.Combine(_fixture.FullName, "Media");
    private LibraryIndex NewIndex() => new(Db, readHeader: _ => new ClipHeader(30, CaptureTime));

    [Fact]
    public async Task Reconcile_OnlyReadsApprovedFilesAndSkipsUnchangedHeaders()
    {
        var read = new List<string>();
        var index = new LibraryIndex(Db, readHeader: path => { read.Add(path); return new(30, CaptureTime); });
        string clip = FileIn("Terraria", "好 clip.mp4");
        FileIn("SteelSeries Moments", "archive.mp4");
        FileIn("Projects", "copy.mp4");
        FileIn("Cache", "preview.mp4");
        FileIn("QuickClip/Originals", "original.mp4");
        FileIn("Other/Skip", "ignored.mp4");
        FileIn("Other", "unfinished.partial.mp4");
        FileIn("Other", "not-a-clip.txt");
        string hidden = FileIn("Other", "hidden.mp4");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        Guid root = await index.RegisterRootAsync(Media, exclusions: ["Other/Skip"]);
        await Assert.ThrowsAsync<ArgumentException>(() => index.RegisterRootAsync(Path.Combine(Media, "SteelSeries Moments")));
        await Assert.ThrowsAsync<ArgumentException>(() => index.RegisterRootAsync(Path.Combine(Media, "Terraria")));
        await Assert.ThrowsAsync<ArgumentException>(() => index.ReconcileAsync(root, new(RelativeDirectory: "../Elsewhere")));
        Assert.Equal(root, await index.RegisterRootAsync(Media, exclusions: ["other\\skip"]));

        var first = await index.ReconcileAsync(root);
        Assert.True(first.Complete);
        Assert.Equal(1, first.HeaderReads);
        Assert.Equal(clip, Assert.Single(read));
        var item = Assert.Single(await index.QueryClipsAsync(new()));
        Assert.Equal("Terraria", item.Game);
        Assert.Equal("好 clip", item.Title);
        Assert.Equal(CaptureDateSource.Embedded, item.DateSource);
        Assert.Equal(CaptureTime, item.CapturedAt);
        Assert.Equal(CaptureTime.Offset, item.CapturedAt.Offset);
        Assert.Equal(0, (await index.ReconcileAsync(root)).HeaderReads);
        Assert.Equal(item, Assert.Single(await index.QueryClipsAsync(new())));
    }

    [Fact]
    public async Task Reconcile_KeepsIdentityThroughRenameAndTouchButSeparatesAReplacement()
    {
        var index = NewIndex();
        string old = FileIn("Initial Game", "clip.mp4");
        Guid root = await index.RegisterRootAsync(Media);
        await index.ReconcileAsync(root);
        var clip = Assert.Single(await index.QueryClipsAsync(new()));
        await index.SetClipDetailsAsync(clip.Id, "Custom", "Corrected", "steam:42");
        await index.SetFavoriteAsync(clip.Id, true);
        await index.SetTagsAsync(clip.Id, ["Highlights"]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        await index.ReconcileAsync(root);
        Assert.Equal(clip.Id, Assert.Single(await index.QueryClipsAsync(new())).Id);
        string moved = Path.Combine(Media, "New Folder", "Clip renamed.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(old, moved);
        FileIn("Initial Game", "clip.mp4");
        await index.ReconcileAsync(root);
        var renamed = Assert.Single(await index.QueryClipsAsync(new() { Tag = "highlights" }));
        Assert.Equal((clip.Id, moved, "Custom", "Corrected", true, clip.CapturedAt),
            (renamed.Id, renamed.Path, renamed.Title, renamed.Game, renamed.Favorite, renamed.CapturedAt));
        Assert.Equal("steam:42", renamed.GameId);
        Assert.NotEqual(clip.Id, Assert.Single(await index.QueryClipsAsync(new() { Game = "Initial Game" })).Id);

        File.Move(moved, Path.ChangeExtension(moved, ".bak"));
        File.WriteAllBytes(moved, [9, 8, 7]);
        await index.ReconcileAsync(root);
        Assert.NotEqual(clip.Id, Assert.Single((await index.QueryClipsAsync(new() { Availability = ClipAvailability.Available }))
            .Where(c => c.Path == moved)).Id);
        Assert.Equal(clip.Id, Assert.Single(await index.QueryClipsAsync(new() { Availability = ClipAvailability.Missing })).Id);
        Assert.Equal("Highlights", Assert.Single((await index.GetAnnotationsAsync(clip.Id)).Tags));
    }

    [Fact]
    public async Task Reconcile_DistinguishesPartialLockedMissingAndOffline()
    {
        var index = NewIndex();
        string a = FileIn("A", "locked.mp4");
        string b = FileIn("B", "removed.mp4");
        Guid root = await index.RegisterRootAsync(Media);
        await index.ReconcileAsync(root);
        File.Delete(b);
        Assert.False((await index.ReconcileAsync(root, new(MaxEntries: 1))).Complete);
        Assert.Empty(await index.QueryClipsAsync(new() { Availability = ClipAvailability.Missing }));
        using (File.Open(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False((await index.ReconcileAsync(root, new(RelativeDirectory: "A"))).Complete);
            Assert.Equal(a, Assert.Single(await index.QueryClipsAsync(new() { Availability = ClipAvailability.Unavailable })).Path);
        }
        Assert.Equal(1, (await index.ReconcileAsync(root, new(RelativeDirectory: "B"))).MarkedMissing);
        await index.ReconcileAsync(root, new(RelativeDirectory: "A"));
        Assert.Equal(a, Assert.Single(await index.QueryClipsAsync(new() { Availability = ClipAvailability.Available })).Path);
        Directory.Move(Media, Media + "-offline");
        var offline = await index.ReconcileAsync(root);
        Assert.Equal(0, offline.MarkedMissing);
        Assert.Equal(ClipAvailability.Unavailable, offline.RootAvailability);
        Assert.Equal(2, (await index.QueryClipsAsync(new() { Availability = ClipAvailability.Unavailable })).Length);
        Directory.Move(Media + "-offline", Media);
        Assert.True((await index.ReconcileAsync(root)).Complete);
        Assert.Equal(b, Assert.Single(await index.QueryClipsAsync(new() { Availability = ClipAvailability.Missing })).Path);
    }

    [Fact]
    public async Task Reconcile_CancellationDoesNotPublishPartOfAScan()
    {
        using var cts = new CancellationTokenSource();
        var index = new LibraryIndex(Db, readHeader: _ => { cts.Cancel(); return new(30, CaptureTime); });
        FileIn("Game", "one.mp4");
        FileIn("Game", "two.mp4");
        Guid root = await index.RegisterRootAsync(Media);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.ReconcileAsync(root, cancellationToken: cts.Token));
        Assert.Empty(await index.QueryClipsAsync(new()));
        Assert.Null(Assert.Single(await index.GetRootsAsync()).LastScanAt);
    }

    [OptInFact("QUICKCLIP_TEST_DISCOVERY_SYMLINK")]
    public async Task Reconcile_NeverFollowsReparseDirectoriesOrFiles()
    {
        var index = NewIndex();
        FileIn("Game", "local.mp4");
        string outside = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "Outside")).FullName;
        string target = Path.Combine(outside, "do-not-index.mp4");
        File.WriteAllBytes(target, [1]);
        string folderLink = Path.Combine(Media, "Linked");
        string fileLink = Path.Combine(Media, "linked.mp4");
        Directory.CreateSymbolicLink(folderLink, outside);
        try
        {
            File.CreateSymbolicLink(fileLink, target);
            try
            {
                Guid root = await index.RegisterRootAsync(Media);
                Assert.True((await index.ReconcileAsync(root)).Complete);
                Assert.Equal("local", Assert.Single(await index.QueryClipsAsync(new())).Title);
                var otherIndex = new LibraryIndex(Path.Combine(_fixture.FullName, "second.sqlite3"));
                Guid linkedRoot = await otherIndex.RegisterRootAsync(folderLink);
                Assert.Equal(ClipAvailability.Unavailable, (await otherIndex.ReconcileAsync(linkedRoot)).RootAvailability);
                Assert.Empty(await otherIndex.QueryClipsAsync(new()));
            }
            finally { File.Delete(fileLink); }
        }
        finally { Directory.Delete(folderLink); }
    }

    [Fact]
    public async Task Reconcile_UsesDefaultMovieHeaderReaderWithoutDecoding()
    {
        string path = FileIn("Game", "header.mp4");
        var boxes = new byte[48];
        BinaryPrimitives.WriteUInt32BigEndian(boxes, 48);
        "moov"u8.CopyTo(boxes.AsSpan(4));
        BinaryPrimitives.WriteUInt32BigEndian(boxes.AsSpan(8), 40);
        "mvhd"u8.CopyTo(boxes.AsSpan(12));
        BinaryPrimitives.WriteUInt32BigEndian(boxes.AsSpan(20), (uint)(CaptureTime.ToUnixTimeSeconds() + 2_082_844_800L));
        BinaryPrimitives.WriteUInt32BigEndian(boxes.AsSpan(28), 1000);
        BinaryPrimitives.WriteUInt32BigEndian(boxes.AsSpan(32), 9500);
        File.WriteAllBytes(path, boxes);
        FileIn("Game", "older.webm");
        var index = new LibraryIndex(Db);
        Guid root = await index.RegisterRootAsync(Media);
        await index.ReconcileAsync(root);
        var clip = Assert.Single(await index.QueryClipsAsync(new() { Search = "header" }));
        Assert.Equal((CaptureTime, CaptureDateSource.Embedded, TimeSpan.FromSeconds(9.5)),
            (clip.CapturedAt, clip.DateSource, clip.Duration));
        var webm = Assert.Single(await index.QueryClipsAsync(new() { Search = "older" }));
        Assert.Null(webm.Duration);
        await Assert.ThrowsAsync<InvalidOperationException>(() => index.UpsertBookmarkAsync(webm.Id,
            new(Guid.NewGuid(), TimeSpan.Zero, null, BookmarkOrigin.Editor)));
    }

    [Fact]
    public async Task LegacyMigration_ImportsMatchingStatsOnceAndReportsMismatches()
    {
        string details = Directory.CreateDirectory(Path.Combine(_fixture.FullName, "details")).FullName;
        var index = new LibraryIndex(Db, details, _ => default);
        string good = FileIn("Game", "valid.mp4");
        string bad = FileIn("Game", "changed.mp4");
        string broken = FileIn("Game", "corrupt.mp4");
        string validJson = WriteDetails(good, false);
        string staleJson = WriteDetails(bad, true);
        File.WriteAllText(DetailsPath(broken), "{");
        Guid root = await index.RegisterRootAsync(Media, MediaCategory.Legacy);
        await index.ReconcileAsync(root);
        var rows = (await index.QueryClipsAsync(new())).ToDictionary(clip => clip.Path);
        Assert.Equal((LegacyMetadataStatus.Imported, CaptureDateSource.LegacyDetails, "Old Title", "Correct Game"),
            (rows[good].LegacyMetadata, rows[good].DateSource, rows[good].Title, rows[good].Game));
        Assert.Equal(CaptureTime, rows[good].CapturedAt);
        Assert.Equal(LegacyMetadataStatus.StatMismatch, rows[bad].LegacyMetadata);
        Assert.Equal(CaptureDateSource.FileTime, rows[bad].DateSource);
        Assert.Equal("changed", rows[bad].Title);
        Assert.Equal(LegacyMetadataStatus.Unreadable, rows[broken].LegacyMetadata);
        await index.SetClipDetailsAsync(rows[good].Id, "New Title", "New Game");
        File.SetLastWriteTimeUtc(good, DateTime.UtcNow.AddHours(-2));
        await index.ReconcileAsync(root);
        var updated = Assert.Single(await index.QueryClipsAsync(new() { Game = "New Game" }));
        Assert.Equal(rows[good].CapturedAt, updated.CapturedAt);
        Assert.Equal("New Title", updated.Title);
        Assert.Equal(validJson, File.ReadAllText(DetailsPath(good)));
        Assert.Equal(staleJson, File.ReadAllText(DetailsPath(bad)));

        string DetailsPath(string path) => Path.Combine(details,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))) + ".json");
        string WriteDetails(string path, bool mismatch)
        {
            var file = new FileInfo(path);
            string json = JsonSerializer.Serialize(new
            {
                CreatedTicks = file.CreationTimeUtc.Ticks, ModifiedTicks = file.LastWriteTimeUtc.Ticks,
                Length = file.Length + (mismatch ? 1 : 0),
                Details = new ClipDetails(CaptureTime, "Correct Game", "Old Title")
            });
            File.WriteAllText(DetailsPath(path), json);
            return json;
        }
    }

    [Fact]
    public async Task Annotations_FilterUpdateAndEnforceRealOffsetsAndRevisions()
    {
        var index = NewIndex();
        FileIn("Game", "A.mp4");
        FileIn("Game", "B.mp4");
        Guid root = await index.RegisterRootAsync(Media);
        await index.ReconcileAsync(root);
        var ordered = new ClipQuery { Sort = ClipSort.Title, Descending = false, Limit = 1 };
        var a = Assert.Single(await index.QueryClipsAsync(ordered));
        var b = Assert.Single(await index.QueryClipsAsync(ordered with { Offset = 1 }));
        Assert.Equal(("A", "B"), (a.Title, b.Title));

        var marker = new ClipBookmark(Guid.NewGuid(), TimeSpan.FromSeconds(8), "Live marker", BookmarkOrigin.Live,
            Guid.NewGuid(), CaptureTime);
        Assert.Equal(1, await index.UpsertBookmarkAsync(a.Id, marker, a.Revision));
        await Assert.ThrowsAsync<LibraryConflictException>(() => index.SetFavoriteAsync(a.Id, true, a.Revision));
        Assert.Equal(2, await index.SetFavoriteAsync(a.Id, true, 1));
        Assert.Equal(3, await index.SetTagsAsync(a.Id, [" Réussi ", "re\u0301ussi", "Highlights"], 2));
        Assert.Equal(4, await index.SetClipDetailsAsync(a.Id, "Réussi % ' clip", "Terraria", "steam:105600", 3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            index.UpsertBookmarkAsync(a.Id, marker with { Offset = TimeSpan.FromSeconds(30) }, 4));
        await Assert.ThrowsAsync<LibraryConflictException>(() => index.UpsertBookmarkAsync(b.Id, marker, 0));
        var annotation = await index.GetAnnotationsAsync(a.Id);
        Assert.Equal(4, annotation.Revision);
        Assert.Equal(new[] { "Highlights", "Réussi" }, annotation.Tags.ToArray());
        Assert.Equal(marker, Assert.Single(annotation.Bookmarks));
        Assert.Equal(0, (await index.GetAnnotationsAsync(b.Id)).Revision);
        var filtered = await index.QueryClipsAsync(new()
        {
            Search = "re\u0301ussi % '", Tag = "réussi", Favorite = true, Game = "terraria", GameId = "steam:105600", RootId = root,
            Category = MediaCategory.ImportedFinished, CapturedFrom = CaptureTime, CapturedBefore = CaptureTime.AddTicks(1),
            MinDuration = TimeSpan.FromSeconds(30), MaxDuration = TimeSpan.FromSeconds(30)
        });
        Assert.Equal(a.Id, Assert.Single(filtered).Id);
        Assert.Empty(await index.QueryClipsAsync(new() { Search = "' OR 1=1 --" }));
        Assert.Equal(5, await index.UpsertBookmarkAsync(a.Id, marker with { Label = "Edited" }, 4));
        Assert.Equal("Edited", Assert.Single((await index.GetAnnotationsAsync(a.Id)).Bookmarks).Label);
        Assert.Equal(6, await index.DeleteBookmarkAsync(a.Id, marker.Id, 5));
        Assert.Equal(7, await index.SetTagsAsync(a.Id, [], 6));
        Assert.Empty((await index.GetAnnotationsAsync(a.Id)).Bookmarks);
        Assert.Empty((await index.GetAnnotationsAsync(a.Id)).Tags);
    }

    [Fact]
    public async Task Inventory_CountsHardLinksOnceAndTracksAvailableExports()
    {
        var index = NewIndex();
        string finished = FileIn("Game", "finished.mp4");
        string original = FileIn("QuickClip/Originals/Game", "saved.mp4");
        string exported = FileIn("QuickClip/Exports/Game", "exported.mp4");
        string link = Path.Combine(Media, "Game", "second-link.mp4");
        ProjectLibrary.LinkOrCopy(finished, link);
        var importedRoot = await index.RegisterRootAsync(Media);
        var originalRoot = await index.RegisterRootAsync(Path.Combine(Media, "QuickClip", "Originals"), MediaCategory.QuickClipOriginal);
        var exportRoot = await index.RegisterRootAsync(Path.Combine(Media, "QuickClip", "Exports"), MediaCategory.Export);
        foreach (Guid root in new[] { importedRoot, originalRoot, exportRoot }) await index.ReconcileAsync(root);

        var inventory = await index.GetStorageInventoryAsync();
        Assert.Equal(4, inventory.AvailableGroups.Sum(g => g.Clips));
        Assert.Equal(16, inventory.AvailableGroups.Sum(g => g.LogicalBytes));
        Assert.Equal(12, inventory.KnownUniqueLogicalBytes);
        Assert.Equal(0, inventory.UnidentifiedLogicalBytes);
        Assert.Equal(1, inventory.OriginalsWithoutExport);
        Assert.All(inventory.RootSpace, volume => Assert.True(volume.FreeBytes > 0));
        var o = Assert.Single(await index.QueryClipsAsync(new() { Category = MediaCategory.QuickClipOriginal }));
        var e = Assert.Single(await index.QueryClipsAsync(new() { Category = MediaCategory.Export }));
        await Assert.ThrowsAsync<ArgumentException>(() => index.SetClipAssociationAsync(o.Id, e.Id, null));
        var session = Guid.NewGuid();
        await index.SetClipAssociationAsync(e.Id, o.Id, session);
        var linked = Assert.Single(await index.QueryClipsAsync(new() { Category = MediaCategory.Export }));
        Assert.Equal((o.Id, session), (linked.OriginalId, linked.SessionId));
        Assert.Equal(0, (await index.GetStorageInventoryAsync()).OriginalsWithoutExport);
        File.Delete(exported);
        await index.ReconcileAsync(exportRoot);
        inventory = await index.GetStorageInventoryAsync();
        Assert.Equal(1, inventory.OriginalsWithoutExport);
        Assert.Equal(1, inventory.MissingClips);
        Assert.True(File.Exists(original));
        Assert.Equal(4_500_000, StorageInventory.EstimateBytes(1000, 200, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Database_MigratesV1AndRestoresSyntheticBackupWithoutOverwriting()
    {
        var index = NewIndex();
        string media = FileIn("Game", "clip.mp4");
        Guid root = await index.RegisterRootAsync(Media);
        await index.ReconcileAsync(root);
        Guid id = Assert.Single(await index.QueryClipsAsync(new())).Id;
        await index.SetFavoriteAsync(id, true);

        using (var connection = new LibraryDatabase(Db).Open())
        {
            LibraryDatabase.Execute(connection, null,
                "DROP TABLE bookmarks; DROP TABLE clip_tags; DROP TABLE tags; PRAGMA user_version=1;");
        }
        Assert.True((await index.GetAnnotationsAsync(id)).Favorite);
        await index.SetTagsAsync(id, ["Preserved"]);
        using (var connection = new LibraryDatabase(Db).Open())
        {
            Assert.Equal(2L, LibraryDatabase.Scalar(connection, null, "PRAGMA user_version;"));
            Assert.Equal("wal", LibraryDatabase.Scalar(connection, null, "PRAGMA journal_mode;"));
            Assert.Equal(1L, LibraryDatabase.Scalar(connection, null, "PRAGMA foreign_keys;"));
        }
        string backup = Path.Combine(_fixture.FullName, "backup.sqlite3");
        await index.BackupAsync(backup);
        await Assert.ThrowsAsync<IOException>(() => index.BackupAsync(backup));
        byte[] damaged = [5, 9, 1, 4, 2];
        File.WriteAllBytes(Db, damaged);
        await Assert.ThrowsAsync<SqliteException>(() => index.QueryClipsAsync(new()));
        Assert.Equal(damaged, File.ReadAllBytes(Db));
        var restored = new LibraryIndex(backup);
        Assert.Equal(id, Assert.Single(await restored.QueryClipsAsync(new())).Id);
        Assert.Equal("Preserved", Assert.Single((await restored.GetAnnotationsAsync(id)).Tags));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(media));
    }

    private string FileIn(string directory, string fileName)
    {
        string path = Path.Combine(Directory.CreateDirectory(Path.Combine(Media, directory)).FullName, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        return path;
    }

    public void Dispose() => _fixture.Delete(recursive: true);
}
