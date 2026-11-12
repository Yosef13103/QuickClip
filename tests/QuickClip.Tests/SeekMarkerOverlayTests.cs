using QuickClip.Controls;

namespace QuickClip.Tests;

public sealed class SeekMarkerOverlayTests
{
    [Theory]
    [InlineData(0, 60, 120, 8)]
    [InlineData(30, 60, 120, 60)]
    [InlineData(60, 60, 120, 112)]
    [InlineData(28, 60, 120, 56.53333333333333)]
    public void Saved_clip_marker_is_placed_at_its_relative_offset(double offset, double duration, double width, double expected)
    {
        Assert.Equal(expected, SeekMarkerOverlay.Position(offset, duration, width), precision: 6);
    }

    [Fact]
    public void Invalid_duration_or_size_does_not_generate_geometry()
    {
        Assert.Equal(0, SeekMarkerOverlay.Position(32, 0, 120));
        Assert.Equal(0, SeekMarkerOverlay.Position(32, 60, 0));
        Assert.Equal(0, SeekMarkerOverlay.Position(double.NaN, 60, 120));
    }
}
