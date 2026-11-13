using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class CaptureAlertPolicyTests
{
    private sealed class Clock : TimeProvider
    {
        private long _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _now;
        internal void Advance(TimeSpan span) => _now += span.Ticks;
    }

    [Fact]
    public void Brief_stall_never_interrupts_user_but_sustained_failure_warns_once()
    {
        var clock = new Clock();
        var policy = new CaptureAlertPolicy(clock);
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.GameTriggered, true, false));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Recovering, true, true));
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Recovering, true, true));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(CaptureAlertKind.Interrupted, policy.Observe(RecordingMode.Recovering, true, true));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Recovering, true, true));
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Recovering, true, true));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CaptureAlertKind.Interrupted, policy.Observe(RecordingMode.Recovering, true, true));
        Assert.Equal(CaptureAlertKind.Restored, policy.Observe(RecordingMode.GameTriggered, true, false));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.GameTriggered, true, false));
    }

    [Fact]
    public void Unavailable_source_warns_immediately_and_healthy_or_disabled_state_resets()
    {
        var policy = new CaptureAlertPolicy(new Clock());
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Unavailable, true, false));
        Assert.Equal(CaptureAlertKind.Interrupted, policy.Observe(RecordingMode.Unavailable, true, true));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Unavailable, true, true));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Off, false, false));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.Armed, true, false));
        Assert.Equal(CaptureAlertKind.None, policy.Observe(RecordingMode.ManualDesktop, true, false));
    }

    [Fact]
    public void Both_channels_are_independent_of_windows_and_clip_event_master()
    {
        var settings = new AppSettings { ClipNotificationsEnabled = false,
            ClipWindowsNotification = false, ClipOverlay = new() { Enabled = true, ImageOnly = true } };
        Assert.Equal((false, true), AppHost.CriticalCaptureChannels(settings));
        settings.ClipWindowsNotification = true;
        Assert.Equal((true, true), AppHost.CriticalCaptureChannels(settings));
        settings.ClipOverlay = new() { Enabled = false };
        Assert.Equal((true, false), AppHost.CriticalCaptureChannels(settings));
    }

    [Fact]
    public void Critical_alert_overrides_image_only_and_short_badge_without_changing_saved_preferences()
    {
        var configured = new ClipOverlayOptions
        {
            Enabled = true, Style = OverlayStyle.Badge, ImageOnly = true, ImagePath = @"C:\Clips\cat.gif",
            Seconds = .6, Size = 48, Anchor = OverlayAnchor.TopLeft,
        };
        var urgent = AppHost.CriticalCaptureOverlayOptions(configured);
        Assert.Equal(OverlayStyle.Compact, urgent.Style);
        Assert.False(urgent.ImageOnly);
        Assert.Null(urgent.ImagePath);
        Assert.Equal(5, urgent.Seconds);
        Assert.Equal(72, urgent.Size);
        Assert.Equal(OverlayAnchor.TopLeft, urgent.Anchor);
        Assert.True(configured.ImageOnly);
        Assert.Equal(.6, configured.Seconds);
    }
}
