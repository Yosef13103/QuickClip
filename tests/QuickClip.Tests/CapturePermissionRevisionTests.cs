using QuickClip.Recording.Companion;

namespace QuickClip.Tests;

public sealed class CapturePermissionRevisionTests
{
    [Fact]
    public void Blocking_and_then_reallowing_a_display_never_restores_an_old_epoch_generation()
    {
        var s = new AppSettings
        {
            AllowRecordingDisplaysByDefault = false,
            AllowedRecordingDisplays = ["DISPLAY-A"],
        };
        int initial = s.CapturePermissionRevision;
        s.AllowedRecordingDisplays = [];
        int revoked = s.CapturePermissionRevision;
        Assert.True(revoked > initial);
        s.AllowedRecordingDisplays = ["DISPLAY-A"];
        Assert.True(s.CapturePermissionRevision > revoked);
        Assert.NotEqual(initial, s.CapturePermissionRevision);
    }

    [Fact]
    public void Blocked_game_displays_and_default_policy_mutations_revoke_old_generations()
    {
        var s = new AppSettings { AllowRecordingDisplaysByDefault = false };
        int initial = s.CapturePermissionRevision;
        s.ExcludedGameDisplays = ["DISPLAY-B"];
        Assert.True(s.CapturePermissionRevision > initial);
        int excluded = s.CapturePermissionRevision;
        s.AllowRecordingDisplaysByDefault = true;
        Assert.True(s.CapturePermissionRevision > excluded);
        int allowed = s.CapturePermissionRevision;
        s.ExcludedGameDisplays = [];
        Assert.True(s.CapturePermissionRevision > allowed);
    }

    [Fact]
    public void Discord_grant_mode_and_mirror_mutations_revoke_detached_history()
    {
        var s = new AppSettings();
        int initial = s.CapturePermissionRevision;
        s.DiscordCaptureEnabled = true;
        int enabled = s.CapturePermissionRevision;
        Assert.True(enabled > initial);
        s.DiscordCaptureMode = DiscordCaptureMode.WhileOpen;
        int mode = s.CapturePermissionRevision;
        Assert.True(mode > enabled);
        s.DiscordCloakedMirror = true;
        Assert.True(s.CapturePermissionRevision > mode);
        s.DiscordCaptureEnabled = false;
        Assert.True(s.CapturePermissionRevision > mode);
    }

    [Fact]
    public void Reassigning_semantically_identical_permission_lists_does_not_churn_the_generation()
    {
        var s = new AppSettings { AllowedRecordingDisplays = ["DISPLAY-A"] };
        int initial = s.CapturePermissionRevision;
        s.AllowedRecordingDisplays = ["display-a"];
        s.ExcludedGameDisplays = [];
        Assert.Equal(initial, s.CapturePermissionRevision);
    }

    [Fact]
    public void Turning_recording_off_revokes_even_if_it_is_immediately_enabled_again()
    {
        var s = new AppSettings { RecordingEnabled = true };
        int initial = s.CapturePermissionRevision;
        s.RecordingEnabled = false;
        int disabled = s.CapturePermissionRevision;
        Assert.True(disabled > initial);
        s.RecordingEnabled = true;
        Assert.Equal(disabled, s.CapturePermissionRevision);
        Assert.True(s.RecordingDisableRevision > 0);
    }

    [Fact]
    public void Capture_grant_revision_is_process_local_and_not_persisted_in_settings_json()
    {
        var settings = new AppSettings { AllowedRecordingDisplays = ["DISPLAY-A"] };
        int revision = settings.CapturePermissionRevision;
        Assert.True(revision > 0);
        string json = settings.ToJson();
        Assert.DoesNotContain("CapturePermissionRevision", json);
        var restored = AppSettings.Parse(json);
        Assert.Equal(settings.AllowedRecordingDisplays, restored.AllowedRecordingDisplays);
        Assert.NotEqual(revision + 100, restored.CapturePermissionRevision);
    }
}
