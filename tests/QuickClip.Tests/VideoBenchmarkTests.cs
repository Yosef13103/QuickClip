using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class VideoBenchmarkTests
{
    [Theory]
    [InlineData(null, 20000)]
    [InlineData(500, 500)]
    [InlineData(250000, 250000)]
    public void Explicit_targets_pass_through_and_auto_uses_the_existing_quality_formula(int? requested, int expected)
    {
        VideoBenchmark.Validate(new(AllowDesktopCapture: true, VideoKbps: requested));
        Assert.Equal(expected, RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 60, "hevc", requested));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(499)]
    [InlineData(250001)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_bitrates_fail_before_capture(int requested)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoBenchmark.Validate(new(AllowDesktopCapture: true, VideoKbps: requested)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingPresets.VideoKbps(RecordingQuality.High, 1920, 1080, 60, "hevc", requested));
    }

    [Theory]
    [InlineData(false, 10, "p4")]
    [InlineData(true, 120, "p4")]
    [InlineData(true, 10, "unsupported")]
    public void Requires_consent_bounded_duration_and_supported_settings(bool consent, int seconds, string preset) =>
        Assert.Throws<ArgumentException>(() => VideoBenchmark.Validate(new(AllowDesktopCapture: consent, Seconds: seconds, Preset: preset)));
}
