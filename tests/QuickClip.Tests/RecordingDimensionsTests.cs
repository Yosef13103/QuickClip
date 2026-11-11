using System.Windows;
using QuickClip.Controls;
using QuickClip.Recording;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class RecordingDimensionsTests
{
    [Theory]
    [InlineData(3440, 1440, "", 3440, 1440, 1440)]
    [InlineData(3440, 1440, "16:9", 2560, 1440, 1440)]
    [InlineData(3440, 1440, "21:9", 3360, 1440, 1440)]
    [InlineData(1920, 1080, "", 1920, 1080, 1080)]
    [InlineData(3840, 2160, "", 3840, 2160, 2160)]
    [InlineData(3840, 2160, "1:1", 2160, 2160, 2160)]
    [InlineData(1080, 1920, "", 1080, 1920, 1080)]
    [InlineData(5120, 1440, "32:9", 5120, 1440, 1440)]
    public void Offers_only_presets_that_fit_the_aligned_capture_area(
        int width, int height, string aspect, int cropWidth, int cropHeight, int maxPreset)
    {
        var area = RecordingPresets.Area(width, height, aspect);
        Assert.Equal((cropWidth, cropHeight), (area.Width, area.Height));
        var presets = RecordingDimensions.AvailableResolutions(area).ToArray();
        Assert.Equal(maxPreset, presets.Max());
        if (maxPreset >= 1440) Assert.Contains(1440, presets);
        Assert.All(presets, preset =>
        {
            var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, preset);
            Assert.InRange(w, 8, cropWidth);
            Assert.InRange(h, 2, cropHeight);
            Assert.Equal(0, w % 8);
            Assert.Equal(0, h % 2);
        });
    }

    [Theory]
    [InlineData(" 32 : 18 ", "16:9")]
    [InlineData("32:9", "32:9")]
    [InlineData("1:1", "1:1")]
    [InlineData("0:9", null)]
    [InlineData("-32:9", null)]
    [InlineData("2147483648:9", null)]
    [InlineData("16:9:1", null)]
    [InlineData("", null)]
    public void Validates_and_normalizes_custom_ratios(string input, string? expected)
    {
        Assert.Equal(expected != null, RecordingDimensions.TryParseAspect(input, out string aspect));
        Assert.Equal(expected ?? "", aspect);
    }

    [Fact]
    public void Resolves_a_locked_pair_and_rejects_stretching_both_values()
    {
        var full = RecordingPresets.Area(3440, 1440, "");
        var crop = RecordingPresets.Area(3440, 1440, "16:9");
        Assert.Equal((1920, 1080), RecordingDimensions.MatchDimension(crop, 1920, editWidth: true));
        Assert.Equal((1920, 1080), RecordingDimensions.MatchDimension(crop, 1080, editWidth: false));
        Assert.Equal((1600, 900), RecordingDimensions.MatchDimension(crop, 900, editWidth: false));
        var portrait = RecordingPresets.Area(1080, 1920, "");
        Assert.Equal((720, 1280), RecordingDimensions.MatchDimension(portrait, 1280, editWidth: false));
        Assert.Equal((280, 498), RecordingDimensions.MatchDimension(portrait, 500, editWidth: false));
        Assert.False(RecordingDimensions.CanProduce(full, 1920, 1080));
        Assert.True(RecordingDimensions.CanProduce(crop, 1920, 1080));
        Assert.False(RecordingDimensions.CanProduce(crop, 2568, 1440));
        Assert.False(RecordingDimensions.CanProduce(crop, 1921, 1080));
        Assert.NotNull(RecordingDimensions.SizeError(1920, 1081, 3440, 1440));
        Assert.NotNull(RecordingDimensions.SizeError(0, 1080, 3440, 1440));
        Assert.NotNull(RecordingDimensions.SizeError(3840, 1080, 3440, 1440));
        Assert.False(RecordingDimensions.TryPixels("2147483648", out _));
        Assert.False(RecordingDimensions.TryPixels("0", out _));
    }

    [Fact]
    public void Resizes_preview_in_layout_units_with_the_same_pixel_crop()
    {
        var area = RecordingPresets.Area(3440, 1440, "16:9");
        var (source, crop) = RecordingAreaPreview.PreviewBounds(3440, 1440, area, new Size(344, 144));
        Assert.Equal(new Rect(0, 0, 344, 144), source);
        Assert.Equal(new Rect(44, 0, 256, 144), crop);
        (source, crop) = RecordingAreaPreview.PreviewBounds(3440, 1440, area, new Size(688, 144));
        Assert.Equal(new Rect(172, 0, 344, 144), source);
        Assert.Equal(new Rect(216, 0, 256, 144), crop);
        Assert.Equal((2560, 1440), RecordingPresets.OutputSize(area.Width, area.Height, 1440));
        Assert.Equal((1920, 1080), RecordingPresets.OutputSize(area.Width, area.Height, 1080));
    }
}
