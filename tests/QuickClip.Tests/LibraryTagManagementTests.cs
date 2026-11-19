using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Tests;

public sealed class LibraryTagManagementTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("qc-tag-management-");
        internal LibraryIndex Index { get; }
        internal string First { get; }
        internal string Second { get; }
        internal string Other { get; }
        internal string Root { get; }
        internal string OtherRoot { get; }
        internal Guid RootId, OtherId;

        internal Fixture()
        {
            Root = _scratch.CreateSubdirectory("clips").FullName;
            OtherRoot = _scratch.CreateSubdirectory("other").FullName;
            First = Path.Combine(Root, "a.mp4");
            Second = Path.Combine(Root, "b.mp4");
            Other = Path.Combine(OtherRoot, "c.mp4");
            foreach (string path in new[] { First, Second, Other }) File.WriteAllText(path, "fixture");
            Index = new(Path.Combine(_scratch.FullName, "library.sqlite3"), null, _ => new ClipHeader(10, null));
        }

        internal async Task InitializeAsync()
        {
            RootId = await Index.RegisterRootAsync(Root);
            OtherId = await Index.RegisterRootAsync(OtherRoot);
            await Index.ReconcileAsync(RootId);
            await Index.ReconcileAsync(OtherId);
        }

        internal async Task<ClipAnnotationTarget> TargetAsync(string path)
        {
            var row = Assert.Single(await Index.ReadGalleryAsync(null, path));
            using var handle = LibraryFiles.OpenMedia(path);
            return new ClipAnnotationTarget(path, row.Stamp, row.Details) { Source = LibraryFiles.ReadStamp(handle) };
        }

        internal async Task SeedAsync(string path, string[] tags)
        {
            var target = await TargetAsync(path);
            await Index.SaveGalleryDetailsAsync(target, target.Details with
            {
                Title = "User title", IsFavorite = true, Tags = tags,
                Bookmarks = [new QuickClip.Gallery.ClipBookmark(2, "Marker")]
            }, default);
        }

        public void Dispose() => _scratch.Delete(recursive: true);
    }

    [Fact]
    public async Task Scoped_inventory_and_bulk_delta_preserve_unrelated_metadata()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.SeedAsync(f.First, ["Funny", "Old"]);
        await f.SeedAsync(f.Second, ["Old"]);
        await f.SeedAsync(f.Other, ["Funny", "Other game"]);
        var a = await f.TargetAsync(f.First);
        var b = await f.TargetAsync(f.Second);
        Assert.Equal(new[] { new LibraryTagUsage("Funny", 1), new LibraryTagUsage("Old", 2) },
            await f.Index.ReadTagUsageAsync([f.RootId]));
        Assert.Empty(await f.Index.ReadTagUsageAsync([]));

        Assert.Equal(2, await f.Index.ApplyTagDeltaAsync([a, b], [" funny ", "New"], ["OLD"]));
        var updated = await f.TargetAsync(f.First);
        Assert.Equal(new[] { "Funny", "New" }, updated.Details.Tags);
        Assert.Equal(a.Details.IndexRevision + 1, updated.Details.IndexRevision);
        Assert.Equal(a.Details.Title, updated.Details.Title);
        Assert.Equal(a.Details.Game, updated.Details.Game);
        Assert.Equal(a.Details.IsFavorite, updated.Details.IsFavorite);
        Assert.Equal(a.Details.Bookmarks, updated.Details.Bookmarks);
        Assert.Equal(a.Source, updated.Source);
        Assert.Equal(new[] { "Funny", "Other game" }, (await f.TargetAsync(f.Other)).Details.Tags);
        Assert.Equal(0, await f.Index.ApplyTagDeltaAsync([updated], ["FUNNY"], ["absent"]));
        Assert.Equal(updated.Details.IndexRevision, (await f.TargetAsync(f.First)).Details.IndexRevision);
        Assert.Equal(new[] { new LibraryTagUsage("Funny", 2), new LibraryTagUsage("New", 2) },
            await f.Index.ReadTagUsageAsync([f.RootId]));
    }

    [Fact]
    public async Task Stale_revision_rolls_back_earlier_edits_and_missing_media_never_edits_a_row()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        var a = await f.TargetAsync(f.First);
        var b = await f.TargetAsync(f.Second);
        await f.Index.SetFavoriteAsync(b.Details.IndexId!.Value, true, b.Details.IndexRevision);
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.ApplyTagDeltaAsync([a, b], ["New"], []));
        var unchanged = await f.TargetAsync(f.First);
        Assert.Equal(a.Details.IndexRevision, unchanged.Details.IndexRevision);
        Assert.Empty(unchanged.Details.Tags);
        var fresh = await f.TargetAsync(f.Second);
        File.AppendAllText(f.Second, "different bytes");
        await Assert.ThrowsAsync<LibraryConflictException>(() =>
            f.Index.ApplyTagDeltaAsync([a, fresh], ["New"], []));
        Assert.Empty((await f.TargetAsync(f.First)).Details.Tags);
    }

    [Fact]
    public async Task Replacement_only_affects_clips_with_old_tag_and_merges_existing_destination()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.SeedAsync(f.First, ["Old", "Keep", "New"]);
        await f.SeedAsync(f.Second, ["Keep"]);
        await f.SeedAsync(f.Other, ["OLD"]);
        var a = await f.TargetAsync(f.First);
        var b = await f.TargetAsync(f.Second);
        var c = await f.TargetAsync(f.Other);
        Assert.Equal(2, await f.Index.ApplyTagDeltaAsync([a, b, c], ["new"], ["old"], replaceOnlyPresent: true));
        Assert.Equal(new[] { "Keep", "New" }, (await f.TargetAsync(f.First)).Details.Tags);
        Assert.Equal(new[] { "Keep" }, (await f.TargetAsync(f.Second)).Details.Tags);
        Assert.Equal(b.Details.IndexRevision, (await f.TargetAsync(f.Second)).Details.IndexRevision);
        Assert.Equal(new[] { "New" }, (await f.TargetAsync(f.Other)).Details.Tags);
        Assert.Equal(a.Details.IndexRevision + 1, (await f.TargetAsync(f.First)).Details.IndexRevision);
    }

    [Fact]
    public async Task Exact_tag_query_is_scoped_and_does_not_match_substrings()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.SeedAsync(f.First, ["Funny"]);
        await f.SeedAsync(f.Second, ["Funny moments"]);
        await f.SeedAsync(f.Other, ["Funny"]);
        var rows = await f.Index.ReadGalleryAsync(f.RootId, null, exactTag: "FUNNY");
        Assert.Equal(f.First, Assert.Single(rows).Path);
        Assert.Equal(2, (await f.Index.ReadGalleryAsync(null, null, exactTag: "Funny")).Count);
        Assert.Empty(await f.Index.ReadGalleryAsync(f.RootId, null, exactTag: "Fun"));
    }

    [Fact]
    public async Task Limit_failure_is_atomic_and_cancelled_work_does_not_commit()
    {
        using var f = new Fixture();
        await f.InitializeAsync();
        await f.SeedAsync(f.Second, Enumerable.Range(0, ClipDetails.MaxTags).Select(i => $"Tag{i}").ToArray());
        var a = await f.TargetAsync(f.First);
        var b = await f.TargetAsync(f.Second);
        await Assert.ThrowsAsync<ArgumentException>(() => f.Index.ApplyTagDeltaAsync([a, b], ["Extra"], []));
        Assert.Empty((await f.TargetAsync(f.First)).Details.Tags);
        Assert.Equal(ClipDetails.MaxTags, (await f.TargetAsync(f.Second)).Details.Tags.Length);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.Index.ApplyTagDeltaAsync([a], ["Extra"], [], cancel.Token));
        Assert.Equal(a.Details.IndexRevision, (await f.TargetAsync(f.First)).Details.IndexRevision);
    }
}
