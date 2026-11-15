using QuickClip.Recording;

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
            AllowGameDisplayFollowing = false, ExcludedGameDisplays = ["test-display"],
        };
        var saved = AppSettings.Parse(settings.ToJson());
        Assert.True(saved.BasicSettings);
        Assert.Equal(4, saved.GuidedSetupStep);
        Assert.False(saved.RecordingEnabled);
        Assert.False(saved.AllowGameDisplayFollowing);
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
}
