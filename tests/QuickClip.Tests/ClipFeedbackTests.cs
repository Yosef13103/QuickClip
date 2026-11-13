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
    public void Event_preferences_preserve_inactive_default_and_classify_failures()
    {
        var settings = AppSettings.Parse(new AppSettings { ClipEvents = new(Saved: false, DesktopStopped: false) }.ToJson());
        Assert.False(settings.ClipEvents.Allows(ClipEvent.Saved, false));
        Assert.True(settings.ClipEvents.Allows(ClipEvent.DesktopStarted, false));
        Assert.False(settings.ClipEvents.Allows(ClipEvent.DesktopStopped, false));
        Assert.False(settings.ClipEvents.Allows(ClipEvent.Inactive, false));
        Assert.True(settings.ClipEvents.Allows(ClipEvent.Inactive, true));
        Assert.False(ClipFeedback.From(settings, ClipEvent.Failed, "Test").Sound);
        Assert.False(ClipFeedback.From(settings, ClipEvent.Inactive, "Test").Success);
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
    public void Overlay_display_falls_back_when_disconnected_and_motion_honors_windows()
    {
        var main = new ClipNotificationOverlay.Display((IntPtr)1, "Display1", new Rect(0, 0, 1920, 1080), true);
        var other = new ClipNotificationOverlay.Display((IntPtr)2, "Display2", new Rect(-1280, 0, 1280, 720), false);
        Assert.Same(other, ClipNotificationOverlay.SelectDisplay([main, other], "display2"));
        Assert.Same(main, ClipNotificationOverlay.SelectDisplay([main], "Display2"));
        Assert.Null(ClipNotificationOverlay.SelectDisplay([], "Display2"));
        Assert.False(ClipNotificationOverlay.MotionEnabled(false, OverlayAnimation.Slide));
        Assert.False(ClipNotificationOverlay.MotionEnabled(true, OverlayAnimation.None));
        Assert.True(ClipNotificationOverlay.MotionEnabled(true, OverlayAnimation.Slide));
    }

    [Theory]
    [InlineData("TopLeft", -344)]
    [InlineData("TopCenter", 344)]
    [InlineData("TopRight", 344)]
    [InlineData("BottomLeft", -344)]
    [InlineData("BottomRight", 344)]
    public void Slide_animation_enters_and_exits_by_more_than_card_width(string anchorName, double expected)
    {
        var anchor = Enum.Parse<OverlayAnchor>(anchorName);
        Assert.Equal(expected, ClipNotificationOverlay.SlideOffset(320, anchor));
        Assert.True(Math.Abs(ClipNotificationOverlay.SlideOffset(320, anchor)) > 320);
    }

    [Fact]
    public void All_enabled_compact_fields_remain_available_for_wrapping()
    {
        var details = new ClipNotificationDetails("Game", "Title", "Display", "60 seconds", "3440 x 1440", "144 FPS", "Alt+F10");
        Assert.Equal(7, details.Lines(new ClipOverlayOptions { Style = OverlayStyle.Compact }).Length);
    }

    [Fact]
    public void Chime_gain_preserves_the_asset_and_applies_to_the_pcm_only()
    {
        byte[] original = ClipSound.Load();
        byte[] half = ClipSoundOutput.ScalePcm(original, 50);
        Assert.Equal(original[..44], half[..44]);
        Assert.Equal(BitConverter.ToInt16(original, 44) / 2, BitConverter.ToInt16(half, 44));
        Assert.All(ClipSoundOutput.ScalePcm(original, 0)[44..], sample => Assert.Equal(0, sample));
        Assert.Same(original, ClipSoundOutput.ScalePcm(original, 100));
        Assert.Equal(100, AppSettings.Parse("{}").ClipSoundVolume);
        Assert.Equal(42, AppSettings.Parse(new AppSettings { ClipSoundVolume = 42 }.ToJson()).ClipSoundVolume);
    }

    private static byte[] GifFixture() => Convert.FromHexString(
        "47494638396101000100800000FF00000000FF" +
        "21F904000A0000002C0000000001000100000202440100" +
        "21F90400190000002C00000000010001000002024C01003B");

    [OptInFact("QUICKCLIP_TEST_NOTIFICATION_WINDOW")]
    public async Task Opt_in_overlay_replaces_and_cancels_notifications_without_a_frame_timer_left_running()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    using var overlay = new ClipNotificationOverlay();
                    var displays = ClipNotificationOverlay.Displays();
                    Assert.NotEmpty(displays);
                    foreach (var display in displays)
                    {
                        await overlay.ShowAsync(new() { Display = display.Device, Seconds = .6, Animation = OverlayAnimation.None }, "First", true);
                        Assert.True(overlay.IsVisible);
                        await overlay.ShowAsync(new() { Display = display.Device, Style = OverlayStyle.Detailed,
                            Seconds = .6, Animation = OverlayAnimation.None }, "Second", false, error: "Fixture");
                        Assert.True(overlay.IsVisible);
                        Assert.False(overlay.IsAnimatingImage);
                        overlay.Hide();
                        Assert.False(overlay.IsVisible);
                        Assert.False(overlay.IsAnimatingImage);
                    }
                    var folder = Directory.CreateTempSubdirectory("quickclip-gif-window-");
                    try
                    {
                        string path = Path.Combine(folder.FullName, "animated.gif");
                        File.WriteAllBytes(path, GifFixture());
                        var options = new ClipOverlayOptions { ImagePath = path, Seconds = .6 };
                        var pending = overlay.ShowAsync(options, "Cancelled", true);
                        overlay.Hide();
                        await pending;
                        Assert.False(overlay.IsVisible);
                        await overlay.ShowAsync(options, "Expires", true);
                        Assert.Equal(SystemParameters.ClientAreaAnimation, overlay.IsAnimatingImage);
                        await Task.Delay(900);
                        Assert.False(overlay.IsVisible);
                        Assert.False(overlay.IsAnimatingImage);
                    }
                    finally { folder.Delete(recursive: true); }
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Background); }
            });
            System.Windows.Threading.Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Gif_uses_individual_frame_delays_and_rejects_corrupt_data()
    {
        // A one-pixel, two-frame GIF with 100 and 250 ms delays.
        byte[] gif = GifFixture();
        var folder = Directory.CreateTempSubdirectory("quickclip-gif-");
        try
        {
            string path = Path.Combine(folder.FullName, "two-frames.gif");
            File.WriteAllBytes(path, gif);
            var image = Assert.IsType<ClipOverlayImage.Asset>(ClipOverlayImage.LoadAsset(path));
            Assert.Equal(new[] { TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250) },
                image.Frames.Select(frame => frame.Delay));
            Assert.All(image.Frames, frame => Assert.True(frame.Bitmap.IsFrozen));
            File.WriteAllBytes(path, gif[..16]);
            Assert.Null(ClipOverlayImage.LoadAsset(path));
        }
        finally { folder.Delete(recursive: true); }
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
