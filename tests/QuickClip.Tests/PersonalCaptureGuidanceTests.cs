using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class PersonalCaptureGuidanceTests
{
    [Theory]
    [InlineData(RecordingMode.Off, true)]
    [InlineData(RecordingMode.Armed, true)]
    [InlineData(RecordingMode.Unavailable, false)]
    [InlineData(RecordingMode.GameTriggered, false)]
    [InlineData(RecordingMode.ManualDesktop, false)]
    public void Inactive_hotkey_notification_does_not_hide_recording_errors(RecordingMode mode, bool expected)
    {
        Assert.Equal(expected, QuickClip.Shell.AppHost.SuppressIdleClipWarning(true, false, mode));
        Assert.False(QuickClip.Shell.AppHost.SuppressIdleClipWarning(true, true, mode));
        Assert.False(QuickClip.Shell.AppHost.SuppressIdleClipWarning(false, false, mode));
    }

    [Fact]
    public void Personal_defaults_to_quiet_save_without_an_active_game()
    {
        if (!AppProfile.IsPersonal) return;
        var settings = AppSettings.Parse("{}");
        Assert.False(settings.ShowInactiveSaveNotifications);
        settings.ShowInactiveSaveNotifications = true;
        Assert.True(AppSettings.Parse(settings.ToJson()).ShowInactiveSaveNotifications);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"PreferGameWindow":true,"RecordingEnabled":true,"RecordMonitor":"display2"}""")]
    public void Fresh_or_saved_game_window_intent_survives_startup_and_dismissal(string json)
    {
        if (!AppProfile.IsPersonal) return;
        var settings = AppSettings.Parse(json);
        string before = settings.ToJson();
        Assert.Null(settings.AuthorizedGameDisplay);
        Assert.False(CaptureSourcePolicy.AcceptGameDisplay(settings, new("display1", "", 0, 0, 1920, 1080), false));
        Assert.False(CaptureSourcePolicy.AcceptGameDisplay(settings, null, true));
        Assert.Equal(before, settings.ToJson());
        Assert.True(AppSettings.Parse(settings.ToJson()).PreferGameWindow);
    }

    [Fact]
    public void Explicit_consent_persists_specific_display_and_DDA_without_enabling_recording()
    {
        var settings = AppSettings.Parse("""{"PreferGameWindow":true,"RecordingEnabled":false}""");
        Assert.True(CaptureSourcePolicy.AcceptGameDisplay(settings, new("display2", "", 1920, 0, 1920, 1080)
        { CaptureIdentity = "synthetic-display-identity" }, true));
        var reloaded = AppSettings.Parse(settings.ToJson());
        Assert.True(reloaded.PreferGameWindow);
        Assert.False(reloaded.RecordingEnabled);
        Assert.Empty(reloaded.RecordMonitor);
        Assert.Equal("synthetic-display-identity", reloaded.AuthorizedGameDisplay);
    }

    [Fact]
    public void Verified_context_survives_Discord_focus_but_not_lost_ownership_or_exclusion()
    {
        var owner = new WindowOwner((IntPtr)101, 10, 20, 30);
        string path = @"C:\Steam\steamapps\common\Fixture\game.exe";
        var entry = new CatalogEntry("steam:12", "Fixture", CatalogSource.Steam, [], [path], new());
        var match = new CatalogMatch(CatalogMatchKind.Candidate, entry);
        var game = GameDetector.Observe(owner, path, match, false, new HashSet<string>());
        var discord = GameDetector.Observe(owner with { Handle = (IntPtr)202 }, @"C:\Apps\Discord.exe",
            new(CatalogMatchKind.Unknown), false, new HashSet<string>());
        Assert.Equal("Fixture", GameDetector.VerifiedGameContext(null, game, candidate => candidate == owner));
        Assert.Equal("Fixture", GameDetector.VerifiedGameContext(game, discord, candidate => candidate == owner, (_, _) => match));
        Assert.Null(GameDetector.VerifiedGameContext(game, discord, _ => false));
        Assert.Null(GameDetector.VerifiedGameContext(game, discord, _ => true, (_, _) => new(CatalogMatchKind.Ignored, entry)));
        Assert.Null(GameDetector.VerifiedGameContext(null, discord, _ => true));
    }

    [Fact]
    public void Context_does_not_imply_a_game_source_and_fixed_display_survives_an_epoch_change()
    {
        var receipt = new ReplaySaveReceipt("fixture.mp4", "Fixture", Guid.Empty, 15, TimeSpan.FromSeconds(12),
            default, default, default, 10, 11, null, []) { GameContext = "Fixture" };
        Assert.True(receipt.SpansCaptureEpochs);
        Assert.Equal("Mixed or unverified sources", receipt.SourceDescription);
        Assert.Equal("Entire display (Desktop Duplication)", (receipt with
        {
            DesktopSource = new("display2", ReplayCaptureBackend.DesktopDuplication),
        }).SourceDescription);
        Assert.Equal("Unverified source", (receipt with { LastSourceId = 10, GameContext = null }).SourceDescription);
    }
}
