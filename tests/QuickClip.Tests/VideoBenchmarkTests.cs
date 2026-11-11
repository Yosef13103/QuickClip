using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class VideoBenchmarkTests
{
    [Theory]
    [InlineData(false, 10, "p4")]
    [InlineData(true, 120, "p4")]
    [InlineData(true, 10, "unsupported")]
    public void Requires_consent_bounded_duration_and_supported_settings(bool consent, int seconds, string preset) =>
        Assert.Throws<ArgumentException>(() => VideoBenchmark.Validate(new(AllowDesktopCapture: consent, Seconds: seconds, Preset: preset)));
}
