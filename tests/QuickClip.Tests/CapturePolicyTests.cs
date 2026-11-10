using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class CapturePolicyTests
{
    private static readonly GameWindowTarget Game = new(new WindowOwner((IntPtr)10, 101, 201, 301), "Game", true);
    private static readonly GameWindowTarget FullscreenApp = new(new WindowOwner((IntPtr)20, 102, 202, 302), "Fullscreen app", false);

    [Theory]
    [InlineData(@"C:\Apps\Discord\Update.exe")]
    [InlineData(@"C:\Apps\DiscordPTB\Update.exe")]
    [InlineData(@"C:\Apps\DiscordCanary\app-1.0\DiscordCanary.exe")]
    public void Discord_and_its_updater_are_not_games_even_when_fullscreen(string path) =>
        Assert.Null(GameDetector.Classify(path, true, new HashSet<string>()));

    [Fact]
    public void A_game_named_update_is_still_classified_by_its_install_path() =>
        Assert.Equal("Example Game", GameDetector.Classify(@"C:\Games\Example Game\Update.exe", true, new HashSet<string>()));

    [Fact]
    public void Focus_on_a_companion_or_unrecognized_fullscreen_window_keeps_the_live_game()
    {
        Assert.Equal(Game, GameDetector.SelectTarget(Game, null, _ => true));
        Assert.Equal(Game, GameDetector.SelectTarget(Game, FullscreenApp, _ => true));
        Assert.Equal(FullscreenApp, GameDetector.SelectTarget(null, FullscreenApp, _ => true));
    }

    [Fact]
    public void A_recognized_game_can_take_over_and_a_reused_window_handle_loses_its_pin()
    {
        var anotherGame = FullscreenApp with { Recognized = true };
        Assert.Equal(anotherGame, GameDetector.SelectTarget(Game, anotherGame, _ => true));

        var replacementOwner = Game.Owner with { Started = Game.Owner.Started + 1 };
        Assert.Null(GameDetector.SelectTarget(Game, null, owner => owner == replacementOwner));
        Assert.Equal(anotherGame, GameDetector.SelectTarget(Game, anotherGame, owner => owner == anotherGame.Owner));
    }

    [Fact]
    public void The_personal_profile_prefers_games_and_preserves_an_explicit_monitor_choice()
    {
        Assert.Equal(AppProfile.IsPersonal, AppSettings.Parse("{}").PreferGameWindow);
        Assert.False(AppSettings.Parse("""{ "PreferGameWindow": false }""").PreferGameWindow);
    }

    [Fact]
    public void Leaving_and_returning_to_the_same_live_game_keeps_its_owner_and_close_releases_it()
    {
        var held = GameDetector.SelectTarget(Game, null, owner => owner == Game.Owner);
        Assert.Equal(Game.Owner, held?.Owner);
        var returned = GameDetector.SelectTarget(held, Game with { Game = "Updated window title" }, owner => owner == Game.Owner);
        Assert.Equal(held?.Owner, returned?.Owner);
        Assert.Null(GameDetector.SelectTarget(returned, null, _ => false));
    }
}
