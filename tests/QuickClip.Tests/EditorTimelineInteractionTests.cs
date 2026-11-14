using System.Windows.Input;
using QuickClip.Controls;

namespace QuickClip.Tests;

public sealed class EditorTimelineInteractionTests
{
    [Fact]
    public void Plain_wheel_is_available_for_vertical_scrolling()
    {
        var state = new TimelineState();
        state.Reset(60);
        Assert.False(TimelineBar.ApplyWheel(state, ModifierKeys.None, 120, 250, 500));
        Assert.Equal(60, state.ViewSpan);
    }

    [Fact]
    public void Ctrl_wheel_zooms_at_pointer_and_shift_wheel_only_pans()
    {
        var state = new TimelineState();
        state.Reset(60);
        double pointer = state.XToTime(200, 500);
        Assert.True(TimelineBar.ApplyWheel(state, ModifierKeys.Control, 120, 200, 500));
        Assert.Equal(48, state.ViewSpan, precision: 6);
        Assert.Equal(pointer, state.XToTime(200, 500), precision: 6);
        double previous = state.ViewStart;
        Assert.True(TimelineBar.ApplyWheel(state, ModifierKeys.Shift, -120, 200, 500));
        Assert.True(state.ViewStart > previous);
        Assert.Equal(48, state.ViewSpan, precision: 6);
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(12, 18, true)]
    [InlineData(-13, 22, true)]
    [InlineData(14, 5, false)]
    [InlineData(5, 23, false)]
    [InlineData(0, -1, false)]
    public void Bookmark_target_is_large_enough_to_click_but_does_not_capture_the_whole_ruler(
        double deltaX, double y, bool expected) =>
        Assert.Equal(expected, TimelineBar.WithinBookmarkHitTarget(deltaX, y));

    [Theory]
    [InlineData(600, 280, 280)]
    [InlineData(450, 600, 305)]
    [InlineData(450, 40, 112)]
    [InlineData(84, 400, 84)]
    public void Timeline_height_reserves_preview_space_when_available(int available, int desired, int expected) =>
        Assert.Equal(expected, TimelineState.ClampEditorHeight(available, desired));
}
