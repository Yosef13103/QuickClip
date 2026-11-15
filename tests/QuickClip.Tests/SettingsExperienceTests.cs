using QuickClip.Recording;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class SettingsExperienceTests
{
    [Fact]
    public void Fresh_personal_defaults_to_basic_setup_without_capture_and_old_profiles_keep_advanced()
    {
        var fresh = new AppSettings();
        Assert.Equal(AppProfile.IsPersonal, fresh.BasicSettings);
        Assert.Equal(AppProfile.IsPersonal ? 0 : (int?)null, fresh.GuidedSetupStep);
        if (AppProfile.IsPersonal) Assert.False(fresh.RecordingEnabled);
        var old = AppSettings.Parse("""{"ReplayOnboardingCompleted":true,"RecordFps":120,"BufferSeconds":90}""");
        Assert.False(old.BasicSettings);
        Assert.Null(old.GuidedSetupStep);
        Assert.Equal(120, old.RecordFps);
        Assert.Equal(90, old.BufferSeconds);
    }

    [Fact]
    public void Presentation_and_progress_roundtrip_without_modifying_advanced_configuration()
    {
        var settings = new AppSettings
        {
            BasicSettings = true, GuidedSetupStep = 4, RecordingEnabled = false,
            RecordEncoder = "hevc_nvenc", RecordFps = 144, RecordAspect = "21:9",
            UseAudioEndpoints = true, EndpointTracks = [new(EndpointFlow.Microphone, "test-mic", "Mic")],
            AllowedRecordingDisplays = ["allowed-display"], ExcludedGameDisplays = ["test-display"],
        };
        var saved = AppSettings.Parse(settings.ToJson());
        Assert.True(saved.BasicSettings);
        Assert.Equal(4, saved.GuidedSetupStep);
        Assert.False(saved.RecordingEnabled);
        Assert.Equal(settings.AllowedRecordingDisplays, saved.AllowedRecordingDisplays);
        Assert.Equal(settings.RecordEncoder, saved.RecordEncoder);
        Assert.Equal(settings.RecordFps, saved.RecordFps);
        Assert.Equal(settings.RecordAspect, saved.RecordAspect);
        Assert.Equal(settings.EndpointTracks, saved.EndpointTracks);
        Assert.Equal(settings.ExcludedGameDisplays, saved.ExcludedGameDisplays);
        saved.BasicSettings = false;
        Assert.Equal(settings.RecordEncoder, saved.RecordEncoder);
        Assert.Equal(settings.EndpointTracks, saved.EndpointTracks);
    }

    [Fact]
    public void Completed_guide_does_not_reopen_and_invalid_step_is_bounded()
    {
        var done = AppSettings.Parse(new AppSettings { ReplayOnboardingCompleted = true, GuidedSetupStep = null }.ToJson());
        Assert.Null(done.GuidedSetupStep);
        Assert.True(done.ReplayOnboardingCompleted);
        Assert.Equal(0, AppSettings.Parse("""{"GuidedSetupStep":999}""").GuidedSetupStep);
    }

    [Fact]
    public void Finish_enable_only_becomes_live_after_persistence_and_rolls_back_on_failure()
    {
        var settings = new AppSettings { GuidedSetupStep = 8, ReplayOnboardingCompleted = false, RecordingEnabled = false };
        Assert.False(SettingsView.SaveGuideCompletion(settings, true, (live, candidate) =>
        {
            Assert.False(live.RecordingEnabled);
            Assert.True(candidate);
            return false;
        }));
        Assert.False(settings.RecordingEnabled);
        Assert.False(settings.ReplayOnboardingCompleted);
        Assert.Equal(8, settings.GuidedSetupStep);
        Assert.True(SettingsView.SaveGuideCompletion(settings, true, (live, candidate) =>
        {
            Assert.False(live.RecordingEnabled);
            Assert.True(candidate);
            return true;
        }));
        Assert.True(settings.RecordingEnabled);
        Assert.True(settings.ReplayOnboardingCompleted);
        Assert.Null(settings.GuidedSetupStep);
        Assert.Empty(settings.AllowedRecordingDisplays);
    }

    [Fact]
    public void Delayed_destination_check_cannot_finish_a_closed_paused_or_reopened_guide()
    {
        Assert.True(SettingsView.CanFinishGuide(true, 4, 4));
        Assert.False(SettingsView.CanFinishGuide(false, 4, 4));
        Assert.False(SettingsView.CanFinishGuide(true, 6, 4));
        Assert.NotNull(SettingsView.CheckDestination(@"\\fixture\share"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Skip_retains_current_recording_and_does_not_grant_a_display(bool alreadyEnabled)
    {
        var settings = new AppSettings { GuidedSetupStep = 3, RecordingEnabled = alreadyEnabled };
        Assert.True(SettingsView.SaveGuideCompletion(settings, false, (_, candidate) => candidate == null));
        Assert.Equal(alreadyEnabled, settings.RecordingEnabled);
        Assert.Empty(settings.AllowedRecordingDisplays);
        Assert.Null(settings.GuidedSetupStep);
    }
}
