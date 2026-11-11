using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class GameCatalogIndexTests
{
    [Fact]
    public async Task A_manual_game_matches_both_paths_but_its_launcher_cannot_claim_gameplay()
    {
        const string play = @"C:\Fixture\Game\Play.exe";
        const string launch = @"C:\Fixture\Game\Launcher.exe";
        var manual = new ManualCatalogEntry(Guid.NewGuid(), "Game", [play, launch, play.ToUpperInvariant()]);
        var request = new CatalogRequest { ManualEntries = [manual] };
        var initial = await GameCatalogService.ScanAsync(request);
        var entry = Assert.Single(initial.Entries);
        Assert.Equal(2, entry.Executables.Length);
        var result = await GameCatalogService.ScanAsync(request with
        {
            Overrides = [new(entry.Id, new("Renamed", Activation: CatalogActivation.ForegroundOnly)
                { LauncherExecutables = [launch] })]
        });

        var index = new GameCatalogIndex(result);
        Assert.Equal(CatalogMatchKind.Launcher, index.Match(launch, true).Kind);
        Assert.Equal(CatalogMatchKind.Inactive, index.Match(play, false).Kind);
        var match = index.Match(play.ToUpperInvariant(), true);
        Assert.Equal(CatalogMatchKind.Game, match.Kind);
        Assert.Equal(entry.Id, match.Entry!.Id);
        Assert.Equal("Renamed", match.Entry.DisplayName);
        Assert.Equal(CatalogMatchKind.Unknown, index.Match(@"C:\Other\Play.exe", true).Kind);
    }

    [Fact]
    public void Explicit_rules_take_precedence_and_conflicting_manual_entries_are_ambiguous()
    {
        const string path = @"C:\Fixture\Game.exe";
        var found = new CatalogEntry("folder:a", "Detected", CatalogSource.Folder, [], [path], new());
        var manual = new CatalogEntry("manual:a", "Manual", CatalogSource.Manual, [], [path],
            new(Inclusion: CatalogInclusion.Exclude));
        Assert.Equal(CatalogMatchKind.Candidate, BuildIndex(found).Match(path, true).Kind);
        Assert.Equal(CatalogMatchKind.Game, BuildIndex(found with { Rules = new(Classification: AppClassification.Game) }).Match(path, true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, BuildIndex(found, manual).Match(path, true).Kind);
        Assert.Equal(CatalogMatchKind.Companion, BuildIndex(found, manual with { Rules = new(Classification: AppClassification.Companion) }).Match(path, true).Kind);
        Assert.Equal(CatalogMatchKind.Application, BuildIndex(found, manual with { Rules = new(Classification: AppClassification.Application) }).Match(path, true).Kind);
        Assert.Equal(CatalogMatchKind.Ambiguous, BuildIndex(manual, found, manual with { Id = "manual:b" }).Match(path, true).Kind);
    }

    [Fact]
    public void Discord_updater_is_ignored_even_if_saved_as_an_explicit_game()
    {
        const string path = @"C:\Fixture\DiscordCanary\Update.exe";
        var entry = new CatalogEntry("manual:a", "Updater", CatalogSource.Manual, [], [path],
            new(Classification: AppClassification.Game));
        Assert.Equal(CatalogMatchKind.Ignored, BuildIndex(entry).Match(path, true).Kind);
    }

    [Fact]
    public async Task Refreshes_are_coalesced_until_the_monotonic_interval_elapses()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new TestClock();
        var refresh = new GameCatalogRefresh(new(), TimeSpan.FromMinutes(2), clock);
        var initial = await Task.WhenAll(refresh.RefreshIfDueAsync(), refresh.RefreshIfDueAsync());
        Assert.Same(initial[0], initial[1]);
        clock.Advance(TimeSpan.FromSeconds(119));
        Assert.Same(initial[0], await refresh.RefreshIfDueAsync());
        clock.Advance(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh.RefreshIfDueAsync(cancellation.Token));
        Assert.NotSame(initial[0], await refresh.RefreshIfDueAsync());
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameCatalogRefresh(new(), TimeSpan.FromSeconds(1)));
    }

    private static GameCatalogIndex BuildIndex(params CatalogEntry[] entries) =>
        new(new([.. entries], [], 0, false, DateTimeOffset.UtcNow));

    private sealed class TestClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
