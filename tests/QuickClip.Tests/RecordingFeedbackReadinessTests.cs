using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class RecordingFeedbackReadinessTests
{
    [Fact]
    public void Delayed_game_notifications_follow_only_ready_current_graphs()
    {
        long? announced = null;
        var game = new ReadyCapture(1, RecordingMode.GameTriggered, "DISPLAY1", "Game", 0, []);
        Assert.False(AppHost.TryAnnounceGame(null, ref announced)); // started but no video
        Assert.True(AppHost.TryAnnounceGame(game, ref announced));
        Assert.False(AppHost.TryAnnounceGame(null, ref announced)); // stopping while callback is queued
        Assert.False(AppHost.TryAnnounceGame(game, ref announced));
        Assert.False(AppHost.TryAnnounceGame(game with { Mode = RecordingMode.ManualDesktop, Epoch = 3 }, ref announced));
        Assert.True(AppHost.TryAnnounceGame(game with { Epoch = 5 }, ref announced));
    }

    [Theory]
    [InlineData(RecordingMode.GameTriggered)]
    [InlineData(RecordingMode.ManualDesktop)]
    public void Replay_cannot_publish_after_revocation_during_mux(RecordingMode mode)
    {
        var settings = new AppSettings { RecordingEnabled = true, AllowedRecordingDisplays = ["physical-1", "physical-2"] };
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.False(Recorder.TryUpdateDisplayPermission(settings, s => CaptureSourcePolicy.SetDisplayPermission(s, "physical-1", false), _ => false));
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.True(Recorder.TryUpdateDisplayPermission(settings, s => CaptureSourcePolicy.SetDisplayPermission(s, "physical-1", false), _ => true));
        Assert.False(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-2", true));
        settings.RecordingEnabled = false;
        Assert.False(Recorder.MaySaveFromDisplay(settings, mode, "physical-2", true));
    }

    [Fact]
    public void Preview_targets_active_capture_monitor_and_falls_back_to_primary()
    {
        var primary = new ClipNotificationOverlay.Display(IntPtr.Zero, @"\\.\DISPLAY1",
            new System.Windows.Rect(0, 0, 1920, 1080), true);
        var secondary = new ClipNotificationOverlay.Display(IntPtr.Zero, @"\\.\DISPLAY2",
            new System.Windows.Rect(-1920, 0, 1920, 1080), false);
        var displays = new[] { primary, secondary };
        var active = new ReadyCapture(9, RecordingMode.Companion, secondary.Device, "Discord", 0, []);
        Assert.Equal(secondary, RecordingIndicator.SelectPreviewDisplay(displays, active));
        Assert.Equal(primary, RecordingIndicator.SelectPreviewDisplay(displays, null));
        Assert.Equal(primary, RecordingIndicator.SelectPreviewDisplay(displays,
            active with { Device = "invalid display" }));
    }

    [Fact]
    public void Discord_recording_hud_resolves_a_real_monitor_without_fabricating_one()
    {
        var main = new WindowOwner((IntPtr)41, 2, 3, 4);
        var popup = new WindowOwner((IntPtr)42, 2, 3, 4);
        var sources = new[] { main, popup };
        Assert.Equal(@"\\.\DISPLAY2", Recorder.ResolveFeedbackDisplay(sources, null,
            owner => owner == main ? @"\\.\DISPLAY2" : @"\\.\DISPLAY1"));
        Assert.Equal(@"\\.\DISPLAY1", Recorder.ResolveFeedbackDisplay(sources, null,
            owner => owner == popup ? @"\\.\DISPLAY1" : null));
        Assert.Null(Recorder.ResolveFeedbackDisplay(sources, null, _ => null));
        Assert.Equal(@"\\.\DISPLAY3", Recorder.ResolveFeedbackDisplay(null,
            @"\\.\DISPLAY3", _ => throw new InvalidOperationException("Only for Discord")));
    }

    [Fact]
    public void Final_readiness_needs_a_connected_authorized_physical_display()
    {
        var settings = new AppSettings { AllowedRecordingDisplays = ["physical-b"], ExcludedGameDisplays = ["physical-a"] };
        var display = new MonitorInfo("DISPLAY1", "Fixture", 0, 0, 1920, 1080) { CaptureIdentity = "physical-a" };
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, []));
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, [display]));
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, [display with { CaptureIdentity = null }]));
        Assert.True(SettingsView.HasAuthorizedDisplay(settings, [display with { CaptureIdentity = "physical-b" }]));
    }
}
