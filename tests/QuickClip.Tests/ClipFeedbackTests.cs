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

    [Fact]
    public void Notification_master_suppresses_visual_channels_and_keeps_sound_separate()
    {
        var settings = new AppSettings
        {
            ClipNotificationsEnabled = false, ClipWindowsNotification = true,
            ClipOverlay = new() { Enabled = true },
        };
        var feedback = ClipFeedback.From(settings, true, "Fixture");
        Assert.Equal(!AppProfile.IsPersonal, feedback.Windows);
        Assert.False(feedback.Overlay);
        Assert.True(feedback.Sound);
    }

    [Fact]
    public void Chime_output_round_trips_and_a_missing_endpoint_never_uses_the_default()
    {
        const string missing = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}";
        var settings = AppSettings.Parse(new AppSettings { ClipSoundOutputId = missing }.ToJson());
        Assert.Null(AppSettings.Parse("{}").ClipSoundOutputId);
        Assert.Equal(missing, settings.ClipSoundOutputId);
        Assert.False(ClipSoundOutput.Play(missing));
    }

    [Fact]
    public void Opt_in_chime_plays_on_an_active_render_endpoint()
    {
        if (Environment.GetEnvironmentVariable("QUICKCLIP_TEST_CHIME_OUTPUT") != "1") return;
        var device = Assert.Single(ClipSoundOutput.Devices().Take(1));
        Assert.False(string.IsNullOrWhiteSpace(device.Name));
        Assert.True(ClipSoundOutput.Play(device.Id));
    }

    [Fact]
    public void Previous_save_tooltip_explains_request_time_without_relabeling_user_bookmarks()
    {
        var mark = new Gallery.ClipBookmark(12, "Clip saved");
        Assert.StartsWith("Clip saved", mark.TimelineTooltip);
        Assert.Contains("save request", (mark with { SessionId = Guid.NewGuid() }).TimelineTooltip);
    }

    [Fact]
    public void Bundled_chime_has_the_pcm_layout_used_by_the_render_client()
    {
        var wav = ClipSound.Load();
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), wav[8..16]);
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal((short)1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal((short)16, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        Assert.Equal(wav.Length - 44, BitConverter.ToInt32(wav, 40));
    }

    [Fact]
    public void Overlay_image_decoder_bounds_the_file_and_dimensions_and_freezes_valid_images()
    {
        var folder = Directory.CreateTempSubdirectory("quickclip-overlay-");
        try
        {
            string imagePath = Path.Combine(folder.FullName, "fixture.png");
            Assert.Null(ClipOverlayImage.Load(imagePath));
            File.WriteAllText(imagePath, "Invalid PNG");
            Assert.Null(ClipOverlayImage.Load(imagePath));
            using (var oversized = File.Create(imagePath)) oversized.SetLength(ClipOverlayImage.MaxBytes + 1);
            Assert.Null(ClipOverlayImage.Load(imagePath));

            void WritePng(int width)
            {
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, 1, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, new byte[width * 4], width * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = File.Create(imagePath);
                encoder.Save(output);
            }

            WritePng(ClipOverlayImage.MaxDimension + 1);
            Assert.Null(ClipOverlayImage.Load(imagePath));
            WritePng(256);
            var image = Assert.IsAssignableFrom<System.Windows.Media.Imaging.BitmapSource>(ClipOverlayImage.Load(imagePath));
            Assert.True(image.IsFrozen);
            Assert.Equal(128, image.PixelWidth);
        }
        finally { folder.Delete(recursive: true); }
    }

    [Fact]
    public void Details_honor_each_field_and_rectangular_overlay_aligns_inside_display()
    {
        var details = new ClipNotificationDetails("Game", "Title", "Display", "30 s");
        Assert.Equal(new[] { "Game", "30 s" }, details.Lines(new() { ShowTitle = false, ShowSource = false }));
        Assert.Equal(new Point(-340, 960),
            ClipOverlayOptions.Place(new Rect(-1920, 0, 1920, 1080), new Size(320, 100), 20, OverlayAnchor.BottomRight));
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
