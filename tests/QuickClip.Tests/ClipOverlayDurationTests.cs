using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class ClipOverlayDurationTests
{
    [Theory]
    [InlineData("0.6")]
    [InlineData("1.4")]
    [InlineData("10.1")]
    [InlineData("60")]
    public void Accepts_custom_seconds_in_the_supported_range(string text)
    {
        Assert.True(ClipOverlayOptions.TryParseSeconds(text, out double seconds));
        Assert.InRange(seconds, ClipOverlayOptions.MinSeconds, ClipOverlayOptions.MaxSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("0.5")]
    [InlineData("60.1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void Rejects_bad_duration_without_saving_it(string text) =>
        Assert.False(ClipOverlayOptions.TryParseSeconds(text, out _));

    [Fact]
    public void User_duration_round_trips_and_normalizes_without_the_old_five_second_cap()
    {
        var settings = AppSettings.Parse(new AppSettings { ClipOverlay = new() { Seconds = 10.1 } }.ToJson());
        Assert.Equal(10.1, settings.ClipOverlay.Normalize().Seconds);
        Assert.Equal(60, (new ClipOverlayOptions { Seconds = 120 }).Normalize().Seconds);
        Assert.Equal(.6, (new ClipOverlayOptions { Seconds = .1 }).Normalize().Seconds);
    }

    [Fact]
    public void Actual_owner_gif_dimensions_fit_the_bounded_work_budget()
    {
        // Reported owner animation: 2.84 MiB, 495x498, 101 frames, 10.10 seconds.
        Assert.True(ClipOverlayImage.WithinGifBudget(495, 498, 101));
        Assert.False(ClipOverlayImage.WithinGifBudget(2048, 2048, 120));
        Assert.False(ClipOverlayImage.WithinGifBudget(495, 498, 121));
        Assert.False(ClipOverlayImage.WithinGifBudget(0, 498, 101));
        Assert.Equal(16 * 1024 * 1024, ClipOverlayImage.MaxBytes);
    }
}
