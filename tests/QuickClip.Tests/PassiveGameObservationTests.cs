using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class PassiveGameObservationTests
{
    private const string First = @"D:\Games\First\First.exe";
    private const string Second = @"E:\Games\Second\Second.exe";

    [Fact]
    public void Background_launch_and_exit_do_not_require_a_foreground_game()
    {
        var game = Window(First, 1, 1000);
        var discord = GameDetector.Observe(Window(@"C:\Apps\Discord.exe", 3, 800).Owner,
            @"C:\Apps\Discord.exe", new(CatalogMatchKind.Companion), false, new HashSet<string>());
        var launch = GameDetector.ResolveEligibleGame(null, discord, _ => true, () => [game], Match);
        Assert.Equal(game.Owner, launch?.VerifiedTarget?.Owner);
        Assert.False(launch?.IsForeground);
        var exit = GameDetector.ResolveEligibleGame(launch, discord, _ => false, () => [game], Match);
        Assert.Null(exit);
    }

    [Theory]
    [InlineData((int)CatalogMatchKind.Ignored)]
    [InlineData((int)CatalogMatchKind.Inactive)]
    [InlineData((int)CatalogMatchKind.Ambiguous)]
    [InlineData((int)CatalogMatchKind.Launcher)]
    public void Rechecks_veto_for_retained_and_passive_windows(int veto)
    {
        var game = Window(First, 1, 1000);
        var launch = GameDetector.ResolveEligibleGame(null, null, _ => true, () => [game], Match);
        Assert.NotNull(launch);
        Assert.Null(GameDetector.ResolveEligibleGame(launch, null, _ => true, () => [game],
            (path, fg) => Match(path, fg) with { Kind = (CatalogMatchKind)veto }));
    }

    [Fact]
    public void Same_game_prefers_largest_window_and_retains_it_without_another_walk()
    {
        var small = Window(First, 1, 10);
        var big = Window(First, 2, 100);
        var selected = GameDetector.ResolveEligibleGame(null, null, _ => true, () => [small, big], Match);
        Assert.Equal(big.Owner, selected?.Owner);
        var retained = GameDetector.ResolveEligibleGame(selected, null, _ => true,
            () => throw new InvalidOperationException("A retained owner needs no window walk."), Match);
        Assert.Equal(big.Owner, retained?.Owner);
    }

    [Fact]
    public void Multiple_game_identities_need_focus_or_retained_ownership()
    {
        var first = Window(First, 1, 10);
        var second = Window(Second, 2, 100);
        Assert.Null(GameDetector.ResolveEligibleGame(null, null, _ => true, () => [first, second], Match));
        var foreground = GameDetector.Observe(second.Owner, Second, Match(Second, true), false, new HashSet<string>());
        Assert.Equal(second.Owner, GameDetector.ResolveEligibleGame(null, foreground, _ => true,
            () => throw new InvalidOperationException("Foreground selection needs no window walk."), Match)?.Owner);
    }

    [Fact]
    public void Active_owner_survives_a_deferred_handoff_until_a_catalog_veto()
    {
        var first = Window(First, 1, 10);
        var second = Window(Second, 2, 100);
        var retained = GameDetector.ResolveEligibleGame(null, null, _ => true, () => [first], Match);
        var foreground = GameDetector.Observe(second.Owner, Second, Match(Second, true), false, new HashSet<string>());

        Assert.Equal(first.Owner, GameDetector.ResolveEligibleGame(retained, foreground, _ => true, () => [],
            Match, preferForeground: (_, _) => false)?.Owner);
        Assert.Equal(second.Owner, GameDetector.ResolveEligibleGame(retained, foreground, _ => true, () => [],
            (path, fg) => path == First ? Match(path, fg) with { Kind = CatalogMatchKind.Inactive } : Match(path, fg),
            preferForeground: (_, _) => false)?.Owner);
    }

    private static PassiveGameWindow Window(string path, int number, long size) =>
        new(new(new IntPtr(number), 10, (uint)(20 + number), 30), path, size);

    private static CatalogMatch Match(string path, bool foreground)
    {
        string id = path == First ? "steam:1" : "steam:2";
        return new(CatalogMatchKind.Candidate, new(id, id, CatalogSource.Steam, [], [path], new()));
    }
}
