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
    [InlineData("TopLeft", -344, 0)]
    [InlineData("TopCenter", 0, -124)]
    [InlineData("TopRight", 344, 0)]
    [InlineData("MiddleLeft", -344, 0)]
    [InlineData("Center", 0, 0)]
    [InlineData("MiddleRight", 344, 0)]
    [InlineData("BottomLeft", -344, 0)]
    [InlineData("BottomCenter", 0, 124)]
    [InlineData("BottomRight", 344, 0)]
    public void Slide_enters_from_nearest_edge_and_never_has_a_center_vector(
        string anchorName, double x, double y)
    {
        var anchor = Enum.Parse<OverlayAnchor>(anchorName);
        Assert.Equal(new Point(x, y), ClipNotificationOverlay.SlideVector(320, 100, anchor));
        if (anchor != OverlayAnchor.Center)
            Assert.True(Math.Abs(x) > 320 || Math.Abs(y) > 100);
        else Assert.Equal(OverlayAnimation.Grow, new ClipOverlayOptions
        {
            Anchor = OverlayAnchor.Center, Animation = OverlayAnimation.Slide,
        }.Normalize().Animation);
    }

    [Fact]
    public void Animated_gif_loops_follow_elapsed_time_not_dispatcher_tick_history()
    {
        // Owner's converted 512x512 test GIF: 29 frames, 40ms each, 1.16 seconds.
        var ends = Enumerable.Range(1, 29).Select(n => TimeSpan.FromMilliseconds(n * 40)).ToArray();
        var cycle = TimeSpan.FromMilliseconds(1160);
        foreach (int cycleNumber in new[] { 0, 1, 2, 17, 1000 })
        {
            var start = TimeSpan.FromTicks(cycle.Ticks * cycleNumber);
            Assert.Equal(0, ClipNotificationOverlay.FrameAtElapsed(start, ends, cycle).Frame);
            Assert.Equal(14, ClipNotificationOverlay.FrameAtElapsed(start + TimeSpan.FromMilliseconds(565), ends, cycle).Frame);
            Assert.Equal(28, ClipNotificationOverlay.FrameAtElapsed(start + TimeSpan.FromMilliseconds(1159), ends, cycle).Frame);
            Assert.Equal(0, ClipNotificationOverlay.FrameAtElapsed(start + cycle, ends, cycle).Frame);
            Assert.True(ClipNotificationOverlay.FrameAtElapsed(start + TimeSpan.FromMilliseconds(1159), ends, cycle).UntilNext > TimeSpan.Zero);
        }
    }

    [Fact]
    public void Retired_badge_settings_fall_back_to_compact_and_image_fit_round_trips()
    {
        var saved = new AppSettings
        {
            ClipOverlay = new()
            {
                Style = OverlayStyle.Badge, ImageFit = OverlayImageFit.Cover,
                Anchor = OverlayAnchor.Center, Animation = OverlayAnimation.Slide,
            },
        };
        var parsed = AppSettings.Parse(saved.ToJson());
        Assert.Equal(OverlayStyle.Compact, parsed.ClipOverlay.Normalize().Style);
        Assert.Equal(OverlayImageFit.Cover, parsed.ClipOverlay.Normalize().ImageFit);
        Assert.Equal(OverlayAnimation.Grow, parsed.ClipOverlay.Normalize().Animation);
        Assert.Equal(OverlayImageFit.Contain, new ClipOverlayOptions
        {
            ImageFit = (OverlayImageFit)999,
        }.Normalize().ImageFit);
    }

    [Fact]
    public void All_enabled_compact_fields_remain_available_for_wrapping()
    {
        var details = new ClipNotificationDetails("Game", "Title", "Display", "60 seconds", "3440 x 1440", "144 FPS", "Alt+F10");
        Assert.Equal(7, details.Lines(new ClipOverlayOptions { Style = OverlayStyle.Compact, ShowHotkey = true }).Length);
    }

    [Fact]
    public void Image_only_style_round_trips_and_legacy_image_only_flag_survives()
    {
        var modern = AppSettings.Parse(new AppSettings { ClipOverlay = new()
        {
            Style = OverlayStyle.ImageOnly, ImagePath = @"C:\Media\alpha.webm",
        } }.ToJson());
        Assert.Equal(OverlayStyle.ImageOnly, modern.ClipOverlay.Normalize().Style);
        var legacy = AppSettings.Parse(new AppSettings { ClipOverlay = new()
        {
            Style = OverlayStyle.Compact, ImageOnly = true, ImagePath = @"C:\Media\cat.gif",
        } }.ToJson());
        Assert.True(legacy.ClipOverlay.ImageOnly);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Legacy_banner_hold_does_not_suppress_images_or_other_banners(bool hideBanner, bool hideImage)
    {
        var legacy = AppSettings.Parse("{}").ClipOverlay;
        Assert.False(legacy.HideSavedBannerFromCapture);
        Assert.False(legacy.HideImageFromCapture);
        var settings = new AppSettings
        {
            ClipOverlay = new()
            {
                Enabled = true, ImagePath = "fixture.gif",
                HideSavedBannerFromCapture = hideBanner, HideImageFromCapture = hideImage,
            },
        };
        var options = AppSettings.Parse(settings.ToJson()).ClipOverlay;
        Assert.Equal(hideBanner, options.ForEvent(ClipEvent.Saved) == null);
        Assert.NotNull((options with { ImageOnly = true }).ImageForEvent(ClipEvent.Saved));
        Assert.NotNull(options.ForEvent(ClipEvent.GameStarted));
    }

    [Fact]
    public void Legacy_recording_icons_require_consent_to_ordinary_capture()
    {
        var settings = AppSettings.Parse("""{"ShowGameRecordingIndicator":true,"ShowRecordingMicrophoneIndicator":true}""");
        Assert.False(RecordingIndicator.IsEnabled(settings));
        Assert.True(settings.ShowRecordingMicrophoneIndicator);
        settings.AllowRecordingIconsInCapture = true;
        Assert.True(RecordingIndicator.IsEnabled(AppSettings.Parse(settings.ToJson())));
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
                        var options = new ClipOverlayOptions { Style = OverlayStyle.ImageOnly, ImagePath = path, Seconds = .6 };
                        var pending = overlay.ShowAsync(options, "Cancelled", true);
                        overlay.Hide();
                        await pending;
                        Assert.False(overlay.IsVisible);
                        await overlay.ShowAsync(options with { HideImageFromCapture = true }, "Excluded image", true, saved: true);
                        Assert.True(overlay.IsVisible);
                        Assert.Equal(SystemParameters.ClientAreaAnimation, overlay.IsAnimatingImage);
                        await overlay.ShowAsync(new ClipOverlayOptions { HideSavedBannerFromCapture = true }, "Held banner", true, saved: true);
                        Assert.False(overlay.IsVisible);
                        Assert.False(overlay.IsAnimatingImage);
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

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Non_save_events_never_use_images_or_detailed_banners(bool banners, bool image, bool unused)
    {
        var configured = new ClipOverlayOptions { Enabled = banners, Style = OverlayStyle.Detailed,
            ImageOnly = image, ImagePath = "test.gif", ImageAppearance = new() { Seconds = 6 } };
        foreach (var kind in Enum.GetValues<ClipEvent>().Where(k => k != ClipEvent.Saved))
        {
            var routed = configured.ForEvent(kind);
            if (!banners) Assert.Null(routed);
            else
            {
                Assert.NotNull(routed);
                Assert.Equal(OverlayStyle.Compact, routed.Style);
                Assert.Null(routed.ImagePath);
                Assert.False(routed.ImageOnly);
                Assert.Equal(1.5, routed.Seconds);
            }
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Clip_saved_image_works_independently_of_banner_channel(bool banners, bool image, bool expected)
    {
        var settings = new AppSettings { ClipNotificationsEnabled = true, ClipWindowsNotification = !banners,
            ClipOverlay = new() { Enabled = banners, Style = OverlayStyle.Detailed, ImageOnly = image,
                ImagePath = "test.gif", Seconds = 1.5, ImageAppearance = new() { Seconds = 6 } } };
        var banner = settings.ClipOverlay.ForEvent(ClipEvent.Saved);
        var celebration = settings.ClipOverlay.ImageForEvent(ClipEvent.Saved);
        Assert.Equal(banners, banner != null);
        Assert.Equal(image, celebration != null);
        var feedback = ClipFeedback.From(settings, ClipEvent.Saved, "saved");
        Assert.Equal(AppProfile.IsPersonal && expected, feedback.Overlay);
        Assert.Equal(AppProfile.IsPersonal && banners, feedback.BannerOverlay != null);
        Assert.Equal(AppProfile.IsPersonal && image, feedback.ImageOverlay != null);
        if (banner != null)
        {
            Assert.Equal(OverlayStyle.Detailed, banner.Style);
            Assert.Equal(1.5, banner.Seconds);
            Assert.Null(banner.ImagePath);
        }
        if (celebration != null)
        {
            Assert.Equal(OverlayStyle.ImageOnly, celebration.Style);
            Assert.Equal(6, celebration.Seconds);
            Assert.Equal("test.gif", celebration.ImagePath);
            Assert.Equal(OverlayAnchor.TopRight, celebration.Anchor);
        }
    }

    [Fact]
    public void Legacy_banner_hold_preserves_ordinary_animation()
    {
        var options = new ClipOverlayOptions { Enabled = true, ImageOnly = true, ImagePath = "sample.gif",
            Animation = OverlayAnimation.Slide, HideSavedBannerFromCapture = true,
            ImageAppearance = new() { Animation = OverlayAnimation.Fade } };
        Assert.Null(options.ForEvent(ClipEvent.Saved));
        Assert.Equal(OverlayAnimation.Slide, options.ForEvent(ClipEvent.GameStarted)!.Animation);
        Assert.Equal(OverlayAnimation.Fade, options.ImageForEvent(ClipEvent.Saved)!.Animation);
        Assert.Equal(OverlayAnimation.Slide, (options with { HideSavedBannerFromCapture = false }).ForEvent(ClipEvent.Saved)!.Animation);
    }

    [Fact]
    public void Same_notification_placement_resolves_primary_alias_and_disconnected_fallback()
    {
        var main = new ClipNotificationOverlay.Display((IntPtr)1, "Display1", new Rect(0, 0, 1920, 1080), true);
        var other = new ClipNotificationOverlay.Display((IntPtr)2, "Display2", new Rect(-1280, 0, 1280, 720), false);
        var banner = new ClipOverlayOptions { Anchor = OverlayAnchor.BottomRight };
        var image = banner with { Display = "display1" };
        Assert.True(ClipOverlayOptions.SamePlacement(banner, image, [main, other]));
        Assert.False(ClipOverlayOptions.SamePlacement(banner, image with { Display = "Display2" }, [main, other]));
        Assert.True(ClipOverlayOptions.SamePlacement(banner, image with { Display = "Disconnected" }, [main, other]));
        Assert.False(ClipOverlayOptions.SamePlacement(banner, image with { Anchor = OverlayAnchor.TopRight }, [main, other]));
        Assert.False(ClipOverlayOptions.SamePlacement(banner, image, []));
    }

    [Fact]
    public void Save_sound_image_and_text_preferences_are_independent()
    {
        foreach (bool master in new[] { false, true })
        foreach (bool text in new[] { false, true })
        foreach (bool image in new[] { false, true })
        foreach (bool sound in new[] { false, true })
        {
            var settings = AppSettings.Parse(new AppSettings
            {
                ClipNotificationsEnabled = master,
                ClipEvents = new(Saved: text),
                ClipSound = sound,
                ClipWindowsNotification = true,
                ClipOverlay = new() { Enabled = true, ImageOnly = image, ImagePath = "fixture.gif",
                    Style = OverlayStyle.Detailed, ImageAppearance = new() },
            }.ToJson());
            var saved = ClipFeedback.From(settings, ClipEvent.Saved, "Fixture");
            Assert.Equal(sound, saved.Sound);
            Assert.Equal(!AppProfile.IsPersonal || master && text, saved.Windows);
            Assert.Equal(AppProfile.IsPersonal && master && text, saved.BannerOverlay != null);
            Assert.Equal(AppProfile.IsPersonal && master && image, saved.ImageOverlay != null);
            Assert.Equal(saved.BannerOverlay != null || saved.ImageOverlay != null, saved.Overlay);
            var started = ClipFeedback.From(settings, ClipEvent.GameStarted, "Fixture");
            Assert.False(started.Sound);
            Assert.Null(started.ImageOverlay);
            Assert.Equal(AppProfile.IsPersonal && master, started.BannerOverlay != null);
        }
    }

    [Fact]
    public void Old_image_only_duration_migrates_without_making_status_banners_long()
    {
        var settings = AppSettings.Parse("""
            {"ClipOverlay":{"Enabled":true,"Style":"ImageOnly","ImagePath":"sample.gif","Seconds":6,"Anchor":"TopLeft","Size":48}}
            """);
        var saved = settings.ClipOverlay.ImageForEvent(ClipEvent.Saved)!;
        var status = settings.ClipOverlay.ForEvent(ClipEvent.DesktopStarted)!;
        Assert.Equal(6, saved.Seconds);
        Assert.Equal(OverlayAnchor.TopLeft, saved.Anchor);
        Assert.Equal(48, saved.Size);
        Assert.Equal(1.5, status.Seconds);
        Assert.Equal(OverlayAnchor.BottomRight, status.Anchor);
        Assert.Equal(settings.ClipOverlay.ImageAppearance, AppSettings.Parse(settings.ToJson()).ClipOverlay.ImageAppearance);
        Assert.NotEqual(OverlayAnchor.Center, (settings.ClipOverlay with { Anchor = OverlayAnchor.Center }).Banner().Anchor);
    }

    [Fact]
    public void Empty_custom_path_selects_bundled_animation_when_available()
    {
        var options = new ClipOverlayOptions { Enabled = false, ImageOnly = true, ImageAppearance = new() };
        if (ClipOverlayImage.HasDefault)
        {
            Assert.Equal(OverlayStyle.ImageOnly, options.ImageForEvent(ClipEvent.Saved)!.Style);
            Assert.Null(options.ImageForEvent(ClipEvent.Saved)!.ImagePath);
            Assert.Equal(OverlayStyle.ImageOnly, (options with { Enabled = true }).ImageForEvent(ClipEvent.Saved)!.Style);
        }
        else
        {
            Assert.Null(options.ImageForEvent(ClipEvent.Saved));
        }
        Assert.Null(options.ForEvent(ClipEvent.Saved));
        Assert.Equal(OverlayStyle.Compact, (options with { Enabled = true }).ForEvent(ClipEvent.Saved)!.Style);
        Assert.Null(options.ImageForEvent(ClipEvent.DesktopStarted));
        Assert.Null((options with { ImageOnly = false }).ImageForEvent(ClipEvent.Saved));
    }

    [Fact]
    public void Bundled_camera_animation_has_exact_owner_bytes_and_decodes_within_budget()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "QuickClip", "QuickClip.csproj"))) dir = dir.Parent;
        bool expected = AppProfile.IsPersonal && dir != null &&
            File.Exists(Path.Combine(dir.FullName, "src", "QuickClip", "Assets", "clip-saved.gif"));
        if (!expected && !ClipOverlayImage.HasDefault) return;
        Assert.True(ClipOverlayImage.HasDefault);
        using var resource = typeof(ClipOverlayImage).Assembly.GetManifestResourceStream(ClipOverlayImage.DefaultResource);
        Assert.NotNull(resource);
        Assert.Equal("9A447241222C1412A058495AC183B662314A6E313B504B6F63C7A362CCCC9FAD",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(resource!)));
        var animation = Assert.IsType<ClipOverlayImage.Asset>(ClipOverlayImage.LoadDefaultAsset());
        Assert.Equal(85, animation.Frames.Length);
        Assert.Equal(2590d, animation.Frames.Sum(frame => frame.Delay.TotalMilliseconds));
        Assert.All(animation.Frames, frame => Assert.True(frame.Bitmap.IsFrozen));
        Assert.True(ClipOverlayImage.WithinGifBudget(512, 512, 85));
    }

    [Theory]
    [InlineData(512, 512, 72, 72)]
    [InlineData(512, 256, 144, 72)]
    [InlineData(256, 512, 36, 72)]
    [InlineData(512, 128, 144, 36)]
    public void Contained_image_uses_only_its_actual_aspect_ratio(int w, int h, double expectedW, double expectedH)
    {
        Assert.Equal(new Size(expectedW, expectedH),
            ClipNotificationOverlay.ImageCanvasSize(w, h, 72, OverlayImageFit.Contain));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void Image_stays_four_physical_pixels_inside_all_monitor_edges(double dpiScale)
    {
        var monitor = new Rect(-1920, -1080, 1920, 1080);
        var size = new Size(Math.Round(72 * dpiScale), Math.Round(72 * dpiScale));
        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
        {
            var p = ClipOverlayOptions.Place(monitor, size, 4, anchor);
            Assert.True(p.X >= monitor.Left + 4 && p.X + size.Width <= monitor.Right - 4);
            Assert.True(p.Y >= monitor.Top + 4 && p.Y + size.Height <= monitor.Bottom - 4);
            int column = (int)anchor % 3, row = (int)anchor / 3;
            if (column == 0) Assert.Equal(monitor.Left + 4, p.X);
            if (column == 2) Assert.Equal(monitor.Right - 4, p.X + size.Width);
            if (row == 0) Assert.Equal(monitor.Top + 4, p.Y);
            if (row == 2) Assert.Equal(monitor.Bottom - 4, p.Y + size.Height);
        }
    }

    [Fact]
    public void Tight_image_crop_uses_shared_bounds_over_all_frames_without_changing_timing()
    {
        ClipOverlayImage.Frame Frame(int x, int y, int ms)
        {
            byte[] pixels = new byte[8 * 8 * 4];
            pixels[(y * 8 + x) * 4 + 3] = 255;
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(8, 8, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 32);
            bitmap.Freeze();
            return new(bitmap, TimeSpan.FromMilliseconds(ms));
        }
        var original = new ClipOverlayImage.Asset([Frame(2, 1, 30), Frame(5, 6, 70)]);
        var tight = Assert.IsType<ClipOverlayImage.Asset>(ClipOverlayImage.Tighten(original));
        Assert.Equal(4, tight.First.PixelWidth);
        Assert.Equal(6, tight.First.PixelHeight);
        Assert.All(tight.Frames, f => { Assert.Equal(4, f.Bitmap.PixelWidth); Assert.Equal(6, f.Bitmap.PixelHeight); Assert.True(f.Bitmap.IsFrozen); });
        Assert.Equal(original.Frames.Select(f => f.Delay), tight.Frames.Select(f => f.Delay));
    }

    [Fact]
    public void Restoring_default_image_resets_only_image_duration_and_path()
    {
        var options = new ClipOverlayOptions { ImagePath = "custom.gif", Seconds = 1.5,
            ImageAppearance = new() { Seconds = 9, Size = 83, Anchor = OverlayAnchor.BottomLeft } };
        var restored = options.RestoreDefaultImage();
        Assert.Null(restored.ImagePath);
        Assert.Equal(2.59, restored.Image().Seconds);
        Assert.Equal(83, restored.Image().Size);
        Assert.Equal(OverlayAnchor.BottomLeft, restored.Image().Anchor);
        Assert.Equal(1.5, restored.Banner().Seconds);
        Assert.Equal(2.59, new ClipImageAppearance().Seconds);
    }

    [Theory]
    [InlineData("24", true)]
    [InlineData("83.5", true)]
    [InlineData("240", true)]
    [InlineData("241", false)]
    [InlineData("NaN", false)]
    public void Custom_overlay_size_has_bounded_validation(string text, bool valid) =>
        Assert.Equal(valid, ClipOverlayOptions.TryParseSize(text, out _));

    [Fact]
    public void Bottom_banners_reserve_taskbar_height_on_mixed_dpi_displays()
    {
        var bounds = new Rect(-1920, 0, 1920, 1080);
        var work = new Rect(-1920, 0, 1920, 1020);
        Assert.Equal(1020, ClipNotificationOverlay.BannerBounds(bounds, work, OverlayAnchor.BottomRight, 1.25, 80).Bottom);
        Assert.Equal(1030, ClipNotificationOverlay.BannerBounds(bounds, bounds, OverlayAnchor.BottomLeft, 1.25, 80).Bottom);
        Assert.Equal(bounds, ClipNotificationOverlay.BannerBounds(bounds, work, OverlayAnchor.TopRight, 1.25, 80));
    }

    [Fact]
    public void Game_start_banner_is_independent_of_clip_saved_and_dot_is_opt_in()
    {
        var s = new AppSettings { ClipEvents = new(Saved: false, GameStarted: true),
            ClipOverlay = new() { Enabled = true, ImageOnly = true }, ClipNotificationsEnabled = true };
        Assert.False(s.ShowGameRecordingIndicator);
        Assert.True(s.ClipEvents.Allows(ClipEvent.GameStarted, false));
        Assert.Equal("Game recording started", ClipFeedback.From(s, ClipEvent.GameStarted, "Fixture").Title);
        Assert.False(ClipFeedback.From(s, ClipEvent.GameStarted, "Fixture").Sound);
        Assert.Equal(OverlayStyle.Compact, s.ClipOverlay.ForEvent(ClipEvent.GameStarted)!.Style);
        Assert.False((s.ClipEvents with { GameStarted = false }).Allows(ClipEvent.GameStarted, false));
    }
}
