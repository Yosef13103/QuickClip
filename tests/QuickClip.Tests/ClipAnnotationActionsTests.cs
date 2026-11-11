using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class ClipAnnotationActionsTests
{
    private static readonly DateTimeOffset Captured = new(2025, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Batch_preserves_existing_annotations_and_reports_replaced_files()
    {
        if (!AppProfile.IsPersonal) return;
        var original = new ClipDetails(Captured, "Terraria", "Boss fight")
        {
            Tags = ["Boss"],
            Bookmarks = [new(2, "Start")],
        };
        var first = Fixture(original);
        var replaced = Fixture(original);
        ClipDetailsStore.Write(first.Path, original);
        File.AppendAllText(replaced.Path, " externally changed");

        var actions = LegacyClipAnnotationActions.Instance;
        var results = await actions.ApplyAsync([ClipAnnotationTarget.For(first), ClipAnnotationTarget.For(replaced)],
            " boss ", true, CancellationToken.None);
        Assert.Null(results[0].Error);
        Assert.NotNull(results[1].Error);
        var read = ClipDetailsStore.Read(new FileInfo(first.Path), "Fallback", null);
        Assert.Equal(original.Title, read.Title);
        Assert.Equal(original.Game, read.Game);
        Assert.Equal(Captured, read.CapturedAt);
        Assert.Equal(original.Bookmarks, read.Bookmarks);
        Assert.Equal("Boss", Assert.Single(read.Tags));
        Assert.True(read.IsFavorite);
        Assert.Equal("fixture", File.ReadAllText(first.Path));

        var stale = actions.SaveDraft(ClipAnnotationTarget.For(first), original with { Title = "Stale draft" });
        Assert.Null(stale.Details);
        Assert.NotNull(stale.Error);
    }

    [Fact]
    public void Searches_file_title_game_and_tags_with_stable_favorite_sort()
    {
        var first = Fixture(new ClipDetails(Captured, "Terraria", "Moon Lord") { Tags = ["boss"] });
        var favorite = Fixture(new ClipDetails(Captured, "Terraria", "Empress") { Tags = ["Boss"], IsFavorite = true });
        var other = Fixture(new ClipDetails(Captured, "Minecraft", "Base") { Tags = ["build"], IsFavorite = true });
        ClipItem[] clips = [first, favorite, other];
        Assert.Equal(new[] { favorite, first }, new GalleryClipFilter("terra", "BO", false, true).Apply(clips, ClipLibrary.AllClips));
        Assert.Equal(favorite, Assert.Single(new GalleryClipFilter("", "boss", true, false).Apply(clips, "Terraria")));
        Assert.Equal(first, Assert.Single(new GalleryClipFilter(Path.GetFileName(first.Path), "", false, false).Apply(clips, ClipLibrary.AllClips)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new GalleryClipFilter("", "", false, false)
            .Apply(clips, ClipLibrary.AllClips, canceled.Token));
    }

    [Fact]
    public void Tag_choices_dedupe_exclude_and_prioritize_prefixes() =>
        Assert.Equal(new[] { "Boss", "boss rush", "final boss" },
            ClipTagChoices.Match(["final boss", "Boss", "boss rush", "BOSS", "boss fight", "chat"], " BO ", ["Boss Fight"]));

    private static ClipItem Fixture(ClipDetails details)
    {
        Directory.CreateDirectory(AppProfile.LocalDirectory);
        string path = Path.Combine(AppProfile.LocalDirectory, Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(path, "fixture");
        return new ClipItem(new FileInfo(path), details, new ClipHeader(3, Captured));
    }
}
