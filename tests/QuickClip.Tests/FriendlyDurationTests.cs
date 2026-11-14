namespace QuickClip.Tests;

public sealed class FriendlyDurationTests
{
    [Theory]
    [InlineData(0, "0 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(25.99, "25 seconds")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "1 minute 1 second")]
    [InlineData(120, "2 minutes")]
    [InlineData(125, "2 minutes 5 seconds")]
    [InlineData(3600, "1 hour")]
    [InlineData(3661, "1 hour 1 minute 1 second")]
    public void Friendly_save_status_uses_explicit_units(double value, string expected) =>
        Assert.Equal(expected, Fmt.FriendlyDuration(value));

    [Fact]
    public void Invalid_duration_is_safe_and_existing_compact_timestamps_are_unchanged()
    {
        Assert.Equal("0 seconds", Fmt.FriendlyDuration(double.NaN));
        Assert.Equal("0 seconds", Fmt.FriendlyDuration(double.NegativeInfinity));
        Assert.Equal("01:00", Fmt.CompactDuration(60));
    }
}
