using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class GalleryMultiTagFilterTests
{
    private static ClipDetails Details(params string[] tags) =>
        new(DateTimeOffset.UnixEpoch, "Game") { Tags = tags };

    [Fact]
    public void Exact_all_requires_every_tag_and_does_not_match_substrings()
    {
        var filter = new GalleryClipFilter("", "", false, false) { ExactTags = ["Funny", "Ranked"] };
        Assert.True(filter.IsFiltered);
        Assert.True(filter.MatchesTags(Details("FUNNY", "ranked", "Other")));
        Assert.False(filter.MatchesTags(Details("Funny")));
        Assert.False(filter.MatchesTags(Details("Funny moments", "Ranked")));
        Assert.False(filter.MatchesTags(Details()));
    }

    [Fact]
    public void Any_matches_either_tag_and_empty_selection_is_unrestricted()
    {
        var filter = new GalleryClipFilter("", "", false, false)
            { ExactTags = ["Café", "Ranked"], MatchAllTags = false };
        Assert.True(filter.MatchesTags(Details("Cafe\u0301")));
        Assert.True(filter.MatchesTags(Details("RANKED")));
        Assert.False(filter.MatchesTags(Details("Casual")));
        var cleared = filter with { ExactTags = [] };
        Assert.False(cleared.IsFiltered);
        Assert.True(cleared.MatchesTags(Details()));
    }
}
