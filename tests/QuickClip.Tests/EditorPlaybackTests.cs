using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class EditorPlaybackTests
{
    [Fact]
    public void Clip_navigation_respects_gallery_order_boundaries_and_current_identity()
    {
        string[] ordered = ["newest.mp4", "middle.mp4", "oldest.mp4"];
        Assert.Equal("newest.mp4", EditorView.AdjacentClip(ordered, "MIDDLE.mp4", -1));
        Assert.Equal("oldest.mp4", EditorView.AdjacentClip(ordered, "middle.mp4", 1));
        Assert.Null(EditorView.AdjacentClip(ordered, "newest.mp4", -1));
        Assert.Null(EditorView.AdjacentClip(ordered, "oldest.mp4", 1));
        Assert.Null(EditorView.AdjacentClip(ordered, "imported.mp4", 1));
        Assert.Null(EditorView.AdjacentClip(ordered, null, -1));
    }

    [Theory]
    [InlineData(true, 0, false)]
    [InlineData(true, 2, true)]
    [InlineData(true, 3, true)]
    [InlineData(true, 4, false)]
    [InlineData(true, 6, false)]
    [InlineData(false, 3, false)]
    public void Native_ab_loop_only_arms_inside_an_explicit_selection(bool enabled, double position, bool active)
    {
        var loop = EditorView.LoopForPosition(enabled, position, 2, 4);
        Assert.Equal(active, loop.HasValue);
        if (loop is { } range) Assert.Equal((2d, 4d), range);
    }
}
