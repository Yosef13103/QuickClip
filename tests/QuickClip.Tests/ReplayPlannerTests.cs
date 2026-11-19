using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class ReplayPlannerTests
{
    private static readonly Guid Session = Guid.Parse("0e1615e7-bfb1-4991-8fbe-a7f740dc81fe");

    [Fact]
    public void Three_sixty_second_saves_have_one_hundred_twenty_seconds_of_unique_coverage()
    {
        var clips = new[] { Clip("c", 60, 120), Clip("b", 30, 90), Clip("a", 0, 60) };
        var cluster = Assert.Single(ReplayPlanner.Find(clips));
        Assert.Equal(new[] { "a", "b", "c" }, cluster.Clips.Select(c => c.Media.Path));
        Assert.Equal(120_000_000, cluster.UnionUs);
        Assert.Equal(180_000_000, cluster.IndividualUs);
        Assert.Equal(60_000_000, cluster.DuplicateUs);
    }

    [Fact]
    public void Duplicate_contained_and_chained_saves_do_not_inflate_the_union()
    {
        var cluster = Assert.Single(ReplayPlanner.Find([
            Clip("long", 0, 60), Clip("duplicate", 0, 60), Clip("contained", 10, 20),
            Clip("chain", 59, 80), Clip("short", 79, 81),
        ]));
        Assert.Equal(81_000_000, cluster.UnionUs);
        Assert.Equal(72_000_000, cluster.DuplicateUs);
    }

    [Fact]
    public void Gaps_and_different_sessions_epochs_or_layouts_stay_independent()
    {
        var a = Clip("a", 0, 60);
        var b = Clip("b", 30, 90);
        var result = ReplayPlanner.Find([
            a, b, Clip("c", 100, 120), Clip("d", 110, 130),
            Clip("other-session", 0, 60) with { Session = Guid.NewGuid() },
            Clip("other-launch", 0, 60) with { StartedUtcUs = 100 },
            Clip("other-native-session", 0, 60) with { NativeSession = 999 },
            Clip("other-source", 0, 60) with { Epoch = 2 },
            Clip("other-audio", 0, 60) with { StreamSignature = "other" },
        ]);
        Assert.Equal(2, result.Length);
        Assert.All(result, cluster => Assert.Equal(2, cluster.Clips.Count));
        Assert.Equal(90_000_000, result[0].UnionUs);
        Assert.Equal(30_000_000, result[1].UnionUs);
    }

    [Fact]
    public void A_contiguous_selection_must_itself_have_connected_coverage_and_fit_the_cap()
    {
        var cluster = Assert.Single(ReplayPlanner.Find([
            Clip("long", 0, 100), Clip("b", 10, 20), Clip("c", 30, 40),
        ]));
        Assert.Throws<ExportException>(() => ReplayPlanner.Subset(cluster, 1, 2, TimeSpan.FromMinutes(2)));
        Assert.Throws<ExportException>(() => ReplayPlanner.Subset(cluster, 0, 1, TimeSpan.FromSeconds(99)));
        Assert.Throws<ExportException>(() => ReplayPlanner.Subset(cluster, 0, 0, TimeSpan.FromMinutes(2)));
        Assert.Equal(100_000_000, ReplayPlanner.Subset(cluster, 0, 2, TimeSpan.FromSeconds(100)).UnionUs);
    }

    private static ReplayProvenance Clip(string name, int first, int end) => new(Guid.NewGuid(),
        new(name, new(1, 1, 1000, name)), Session, 123, 1_700_000_000_000_000, 1,
        first * 1_000_000L, end * 1_000_000L, "layout", "hash");
}
