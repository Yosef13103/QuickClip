using System.Windows;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class ClipFeedbackTests
{
    [Theory]
    [InlineData("TopLeft", -1900, 120)]
    [InlineData("TopCenter", -1000, 120)]
    [InlineData("TopRight", -100, 120)]
    [InlineData("MiddleLeft", -1900, 600)]
    [InlineData("Center", -1000, 600)]
    [InlineData("MiddleRight", -100, 600)]
    [InlineData("BottomLeft", -1900, 1080)]
    [InlineData("BottomCenter", -1000, 1080)]
    [InlineData("BottomRight", -100, 1080)]
    public void Places_all_nine_anchors_on_a_negative_origin_display(string anchor, double x, double y) =>
        Assert.Equal(new Point(x, y), ClipOverlayOptions.Place(new Rect(-1920, 100, 1920, 1080), 80, 20, Enum.Parse<OverlayAnchor>(anchor)));

    [Fact]
    public void Notification_channels_round_trip_independently()
    {
        var settings = AppSettings.Parse("""
            {"ClipSound":false,"ClipWindowsNotification":false,
             "ClipOverlay":{"Enabled":true,"Display":"Fixture display","Anchor":"MiddleLeft","Size":88,"Opacity":0.7,"Seconds":2,"Animation":"Fade"}}
            """);
        Assert.False(settings.ClipSound);
        Assert.False(settings.ClipWindowsNotification);
        Assert.True(settings.ClipOverlay.Enabled);
        Assert.Equal("Fixture display", settings.ClipOverlay.Display);
        Assert.Equal(OverlayAnchor.MiddleLeft, settings.ClipOverlay.Anchor);
        Assert.Equal(88, settings.ClipOverlay.Size);
        Assert.Equal(.7, settings.ClipOverlay.Opacity);
        Assert.Equal(2, settings.ClipOverlay.Seconds);
        Assert.Equal(OverlayAnimation.Fade, settings.ClipOverlay.Animation);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Failure_never_plays_the_saved_sound(bool success, bool configured, bool sound)
    {
        var feedback = ClipFeedback.From(new AppSettings { ClipSound = configured, ClipWindowsNotification = false }, success, "Fixture");
        Assert.Equal(sound, feedback.Sound);
        Assert.False(feedback.Windows);
        Assert.Equal(success ? "Clip saved" : "Clip not saved", feedback.Title);
    }
}
