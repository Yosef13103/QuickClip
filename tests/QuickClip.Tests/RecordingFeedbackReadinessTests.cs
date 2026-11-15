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
        var settings = new AppSettings { RecordingEnabled = true, AllowGameDisplayFollowing = true };
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.False(Recorder.TryUpdateDisplayPermission(settings, s => s.ExcludedGameDisplays = ["physical-1"], _ => false));
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.True(Recorder.TryUpdateDisplayPermission(settings, s => s.ExcludedGameDisplays = ["physical-1"], _ => true));
        Assert.False(Recorder.MaySaveFromDisplay(settings, mode, "physical-1", true));
        Assert.True(Recorder.MaySaveFromDisplay(settings, mode, "physical-2", true));
        settings.RecordingEnabled = false;
        Assert.False(Recorder.MaySaveFromDisplay(settings, mode, "physical-2", true));
    }

    [Fact]
    public void Final_readiness_needs_a_connected_authorized_physical_display()
    {
        var settings = new AppSettings { AllowGameDisplayFollowing = true, ExcludedGameDisplays = ["physical-a"] };
        var display = new MonitorInfo("DISPLAY1", "Fixture", 0, 0, 1920, 1080) { CaptureIdentity = "physical-a" };
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, []));
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, [display]));
        Assert.False(SettingsView.HasAuthorizedDisplay(settings, [display with { CaptureIdentity = null }]));
        Assert.True(SettingsView.HasAuthorizedDisplay(settings, [display with { CaptureIdentity = "physical-b" }]));
    }
}
