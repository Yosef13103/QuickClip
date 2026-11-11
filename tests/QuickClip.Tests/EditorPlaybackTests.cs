using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class EditorPlaybackTests
{
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
