using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class ClipTagEditTests
{
    private static ClipDetails Details(params string[] tags) => new(DateTimeOffset.UnixEpoch, "Game", "Title")
    {
        Tags = tags, IsFavorite = true, Bookmarks = [new(1, "Marker")],
        IndexId = Guid.NewGuid(), IndexRevision = 5,
    };

    [Fact]
    public void Counts_are_per_clip_and_case_insensitive()
    {
        var counts = ClipTagEdit.Counts([Details("Funny", "FUNNY", "Ranked"), Details("funny")]);
        Assert.Equal(2, counts.Length);
        Assert.Equal(2, counts[0].Count);
        Assert.Equal("Ranked", counts[1].Name);
        Assert.Equal(1, counts[1].Count);
    }

    [Fact]
    public void Replace_merges_existing_tag_and_preserves_other_annotations()
    {
        var old = Details("Funny", "Keep", "Ranked");
        var draft = new ClipTagEdit("funny", "ranked").Apply(old);
        Assert.Equal(new[] { "Keep", "Ranked" }, draft.Tags);
        Assert.Equal(old, draft with { Tags = old.Tags });
        Assert.Equal(new[] { "Funny", "Keep", "Ranked" }, old.Tags);
        Assert.Same(old, new ClipTagEdit("Funny", "FUNNY").Apply(old));
        Assert.Same(old, new ClipTagEdit("Absent", "New").Apply(old));
    }

    [Fact]
    public void Removal_and_replacement_at_limit_do_not_discard_other_tags()
    {
        var old = Details(Enumerable.Range(0, ClipDetails.MaxTags).Select(i => $"Tag{i}").ToArray());
        var draft = new ClipTagEdit("Tag0", "New").Apply(old);
        Assert.Equal(ClipDetails.MaxTags, draft.Tags.Length);
        Assert.Contains("New", draft.Tags);
        Assert.DoesNotContain("Tag0", draft.Tags);
        Assert.Equal(ClipDetails.MaxTags - 1, new ClipTagEdit("Tag0", " ").Apply(old).Tags.Length);
        Assert.Throws<ArgumentException>(() => new ClipTagEdit("Tag0", new string('x', ClipDetails.MaxTagLength + 1)));
    }

    [Fact]
    public async Task Only_affected_targets_are_saved_and_conflicts_remain_visible()
    {
        var actions = new Actions();
        var affected = new ClipAnnotationTarget("affected", default, Details("Funny", "Keep"));
        var absent = new ClipAnnotationTarget("absent", default, Details("Keep"));
        var result = Assert.Single(await new ClipTagEdit("Funny", null).SaveAsync(actions, [affected, absent], default));
        Assert.Equal("Stale revision", result.Error);
        var call = Assert.Single(actions.Calls);
        Assert.Same(affected, call.Target);
        Assert.Equal(new[] { "Keep" }, call.Draft.Tags);
        Assert.Equal(affected.Details.IndexRevision, call.Draft.IndexRevision);
    }

    private sealed class Actions : IClipAnnotationActions
    {
        internal List<(ClipAnnotationTarget Target, ClipDetails Draft)> Calls { get; } = [];
        public Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
        {
            Calls.Add((target, draft));
            return Task.FromResult(new ClipAnnotationResult(target.Path, null, "Stale revision"));
        }

        public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
            string? addTag, bool? favorite, CancellationToken token) => throw new NotSupportedException();
    }
}
