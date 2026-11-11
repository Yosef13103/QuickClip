using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class GameObservationTests
{
    private static readonly WindowOwner Original = new(new IntPtr(101), 10, 20, 30);
    private const string Renderer = @"C:\Games\Fixture\renderer.exe";
    private static readonly CatalogEntry Entry = new("manual:fixture", "Fixture Game", CatalogSource.Manual,
        [@"C:\Games\Fixture"], [Renderer], new());
    private static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Renderer };

    [Theory]
    [InlineData(0, true, true, true, false)]
    [InlineData(1, false, true, true, false)]
    [InlineData(1, true, false, true, false)]
    [InlineData(2, false, false, false, true)]
    [InlineData(2, true, false, true, false)]
    [InlineData(3, true, true, false, true)]
    [InlineData(4, true, true, false, true)]
    [InlineData(5, true, true, false, true)]
    [InlineData(6, true, true, false, true)]
    [InlineData(7, true, true, false, true)]
    [InlineData(8, true, true, false, true)]
    [InlineData(9, true, true, false, false)]
    public void Catalog_precedence_controls_promotion(int kind, bool fullscreen, bool known, bool promoted, bool blocked)
    {
        CatalogMatchKind[] kinds = [CatalogMatchKind.Game, CatalogMatchKind.Candidate, CatalogMatchKind.Candidate,
            CatalogMatchKind.Ignored, CatalogMatchKind.Ambiguous, CatalogMatchKind.Launcher, CatalogMatchKind.Application,
            CatalogMatchKind.Inactive, CatalogMatchKind.Ignored, CatalogMatchKind.Companion];
        var observed = GameDetector.Observe(Original, Renderer, new(kinds[kind], Entry), fullscreen,
            known ? Known : new HashSet<string>());
        Assert.Equal(promoted ? "Fixture Game" : null, observed.Target?.Game);
        Assert.Equal(blocked, observed.SkipAutomaticCapture);
    }

    public static IEnumerable<object[]> FocusCases()
    {
        string[] apps = [@"C:\Apps\Discord.exe", @"C:\Apps\DiscordPTB.exe", @"C:\Apps\DiscordCanary.exe",
            @"C:\Apps\Discord\app-1\helper.exe", @"C:\Apps\chrome.exe", @"C:\Apps\Update.exe"];
        foreach (string app in apps)
            for (int transition = 0; transition < 5; transition++) yield return [app, transition];
    }

    [Theory]
    [MemberData(nameof(FocusCases))]
    public void Retains_or_replaces_only_a_verified_owner(string app, int transition)
    {
        var match = new CatalogMatch(CatalogMatchKind.Game, Entry);
        var current = GameDetector.Observe(Original, Renderer, match, false, Known);
        var foreground = GameDetector.Observe(new(new IntPtr(222), 11, 21, 31), app, new(CatalogMatchKind.Unknown), true, Known);
        var replacement = Original with { Handle = new IntPtr(333) };
        var restarted = Original with { Started = 50 };
        if (transition is 2 or 3)
            foreground = GameDetector.Observe(transition == 2 ? replacement : restarted, Renderer, match, false, Known);
        var chosen = GameDetector.SelectObservation(current, foreground,
            owner => transition == 0 ? owner == Original : transition is 2 or 3 && owner == foreground.Owner,
            (_, _) => match);
        Assert.Equal(transition switch { 0 => Original, 2 => replacement, 3 => restarted, _ => (WindowOwner?)null }, chosen?.Owner);
    }

    [Theory]
    [InlineData(@"C:\Apps\Discord.exe")]
    [InlineData(@"C:\Apps\DiscordCanary.exe")]
    [InlineData(@"C:\Apps\DiscordPTB.exe")]
    [InlineData(@"C:\Apps\DiscordDevelopment.exe")]
    [InlineData(@"C:\Apps\DiscordOverlay.exe")]
    [InlineData(@"C:\Apps\DiscordHookHelper64.exe")]
    [InlineData(@"C:\Apps\Discord\Update.exe")]
    [InlineData(@"C:\Apps\Discord\app-1\helper.exe")]
    [InlineData(@"C:\Apps\steam.exe")]
    [InlineData(@"C:\Games\Fixture\Launcher.exe")]
    public void Fullscreen_or_manual_association_does_not_promote_companions_or_launchers(string path)
    {
        Assert.Null(GameDetector.Observe(Original, path, new(CatalogMatchKind.Game, Entry), true, Known).Target);
    }

    [Fact]
    public void Disabling_is_persisted_without_erasing_the_entry()
    {
        var id = Guid.NewGuid();
        string key = "manual:" + id.ToString("N");
        var settings = new AppSettings { Games = new() { Request = new()
        {
            ManualEntries = [new(id, Entry.Name, [Renderer])],
            Overrides = [new(key, new(Inclusion: CatalogInclusion.Exclude))],
        } } };
        var reloaded = AppSettings.Parse(settings.ToJson());
        var entries = GameCatalogRuntime.Rows(reloaded.Games);
        var match = new GameCatalogIndex(new(entries, [], 0, false, default)).Match(Renderer, true);
        Assert.Equal(CatalogMatchKind.Ignored, match.Kind);
        Assert.Equal(key, match.Entry?.Id);
    }

    [Fact]
    public void Offline_scan_preserves_the_old_row_and_marks_it_for_review()
    {
        var found = Entry with { Source = CatalogSource.Folder, InstallDirectories = [@"C:\Games\Fixture"] };
        var settings = new GameCatalogSettings { Request = new() { GameRoots = [new(@"C:\Games\Fixture")] },
            Snapshot = new([found], [], 1, false, DateTimeOffset.UtcNow) };
        var next = GameCatalogRuntime.WithScan(settings,
            new([], [new(@"C:\Games\Fixture", CatalogIssueKind.Unavailable)], 0, false, DateTimeOffset.UtcNow));
        Assert.Equal(found.Id, Assert.Single(GameCatalogRuntime.Rows(next)).Id);
        Assert.Contains(found.Id, next.OfflineIds);
        Assert.Empty(GameCatalogRuntime.Rows(next with { Request = new() }));
    }

    [Fact]
    public void Partial_scan_retains_previously_approved_renderer_and_veto()
    {
        string root = @"C:\Steam";
        var found = Entry with { Source = CatalogSource.Steam, InstallDirectories = [root],
            Executables = [Renderer, @"C:\Steam\other.exe"] };
        var settings = new GameCatalogSettings { Request = new() { SteamLibraryRoots = [root, @"C:\Games"],
            Overrides = [new(found.Id, new(Inclusion: CatalogInclusion.Exclude))] },
            Snapshot = new([found], [], 2, false, default) };
        var scan = new CatalogResult([found with { Executables = [@"C:\Steam\other.exe"] }], [], 1, true, default);
        var merged = GameCatalogRuntime.WithScan(settings, scan);
        Assert.Contains(found.Id, merged.OfflineIds);
        Assert.Equal(CatalogMatchKind.Ignored, new GameCatalogIndex(new(GameCatalogRuntime.Rows(merged), [], 0, false, default)).Match(Renderer, true).Kind);
        var verified = GameCatalogRuntime.WithScan(merged, scan with { LimitReached = false });
        Assert.Empty(verified.OfflineIds);
        Assert.DoesNotContain(Renderer, Assert.Single(GameCatalogRuntime.Rows(verified)).Executables);
    }

    [Fact]
    public void Windowed_steam_renderer_keeps_identity_and_rename_while_discord_has_focus()
    {
        string steamExe = @"C:\Steam\steamapps\common\Fixture\renderer.exe";
        var steam = Entry with { Source = CatalogSource.Steam, Executables = [steamExe] };
        var current = GameDetector.Observe(Original, steamExe, new(CatalogMatchKind.Candidate, steam), false, new HashSet<string>());
        Assert.Equal("Fixture Game", current.Target?.Game);
        var companion = GameDetector.Observe(Original with { Handle = new IntPtr(404) },
            @"C:\Apps\Discord.exe", new(CatalogMatchKind.Unknown), false, Known);
        var renamed = GameDetector.SelectObservation(current, companion, owner => owner == Original,
            (_, _) => new(CatalogMatchKind.Candidate, steam with { Rules = new(DisplayName: "Renamed") }));
        Assert.Equal(Original, renamed?.Target?.Owner);
        Assert.Equal("Renamed", renamed?.Target?.Game);
        Assert.Null(GameDetector.SelectObservation(renamed, companion, _ => true, (_, _) => new(CatalogMatchKind.Ignored, steam)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unverified_game_backend_never_switches_to_the_display(bool borderApi)
    {
        var denied = GameDetector.Observe(Original, Renderer, new(CatalogMatchKind.Ignored, Entry), false, Known);
        var automatic = CaptureSourcePolicy.ResolvePersonal(true, CaptureEngine.MonitorBackend.DesktopDuplication, null, denied, borderApi);
        Assert.False(automatic.Allowed);
        Assert.Contains("ineligible", automatic.Error);
        var explicitDisplay = CaptureSourcePolicy.ResolvePersonal(false, CaptureEngine.MonitorBackend.DesktopDuplication, null, denied, borderApi);
        Assert.True(explicitDisplay.Allowed);
        Assert.Equal(CaptureEngine.MonitorBackend.MonitorOnlyDuplication, explicitDisplay.Backend);
    }

    [Fact]
    public void Endpoint_plan_keeps_discord_and_game_playback_separate_from_the_microphone()
    {
        var settings = new AppSettings { UseAudioEndpoints = true, EndpointTracks =
        [
            new(EndpointFlow.Playback, "fixture-sonar-game", "Game"),
            new(EndpointFlow.Playback, "fixture-sonar-chat", "Discord voice and stream"),
            new(EndpointFlow.Microphone, "fixture-sonar-mic", "Microphone"),
            new(EndpointFlow.Playback, "FIXTURE-SONAR-CHAT", "Duplicate"),
        ] };
        Assert.Equal(new[] { "Game", "Discord voice and stream", "Microphone" },
            EndpointAudio.Plan(settings)!.Select(track => track.Label));
    }
}
