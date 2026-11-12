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
    [InlineData(600, 280, 280)]
    [InlineData(450, 600, 305)]
    [InlineData(450, 40, 112)]
    [InlineData(84, 400, 84)]
    public void Timeline_height_reserves_preview_space_when_available(int available, int desired, int expected) =>
        Assert.Equal(expected, TimelineState.ClampEditorHeight(available, desired));
}
