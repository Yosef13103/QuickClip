using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class GogDiscoveryTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-gog-");

    [Fact]
    public async Task Opt_in_discovers_base_products_once_and_never_executes_files()
    {
        string game = Install("Moon");
        string exe = Path.Combine(game, "Moon.exe");
        int reads = 0;
        GameCatalogSettings state = new() { AutoScan = false };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, () => [], () => null, _ =>
            {
                reads++;
                return
                [
                    new("7001", "Moon Game", game),
                    new("7001", "Duplicate", game.ToUpperInvariant()),
                    new("7002", "Expansion", game, Parent: "7001"),
                ];
            }));
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(0, reads);
        coordinator.Start();
        state = state with { DiscoverGog = true };
        await coordinator.SettingsChangedAsync();

        var row = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal("gog:7001", row.Id);
        Assert.Equal("Moon Game", row.DisplayName);
        Assert.Equal(exe, Assert.Single(row.Executables));
        Assert.Equal(CatalogMatchKind.Game, Match(state, exe));
        Assert.Equal(1, reads);
        state = state with { DiscoverGog = false };
        await coordinator.SettingsChangedAsync();
        Assert.Empty(GameCatalogRuntime.Rows(state));
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData((int)CatalogIssueKind.SourceMissing)]
    [InlineData((int)CatalogIssueKind.Unavailable)]
    [InlineData((int)CatalogIssueKind.InvalidManifest)]
    public async Task Provider_failures_keep_identity_and_override_but_revoke_unverified_paths(int kind)
    {
        var issue = (CatalogIssueKind)kind;
        string install = Install("Game");
        string path = Path.Combine(install, "Game.exe");
        bool fail = false;
        GameCatalogSettings state = new()
        {
            AutoScan = false,
            DiscoverGog = true,
            Request = new() { Overrides = [new("gog:10", new("My label"))] },
        };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, () => [], () => null, _ => fail
                ? [new(Issue: issue)] : [new("10", "Registry title", install)]));
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(CatalogMatchKind.Game, Match(state, path));
        fail = true;
        await coordinator.RequestAsync(manual: true);
        var cached = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal("gog:10", cached.Id);
        Assert.Equal("My label", cached.DisplayName);
        Assert.Equal(CatalogMatchKind.Inactive, Match(state, path));
        Assert.Equal(issue, Assert.Single(Assert.Single(state.SourceClaims!.Value).Result.Issues).Kind);
    }

    [Fact]
    public async Task Explicit_folder_disable_survives_overlap_and_a_provider_failure_does_not_revoke_the_folder()
    {
        string install = Install("Game");
        string exe = Path.Combine(install, "Game.exe");
        bool fail = false;
        GameCatalogSettings state = new()
        {
            AutoScan = false,
            DiscoverGog = true,
            Request = new() { GameRoots = [new(install)] },
        };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, () => [], () => null, _ => fail
                ? [new(Issue: CatalogIssueKind.Unavailable)] : [new("65", "GOG Game", install)]));
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(2, state.SourceClaims!.Value.Length);
        Assert.Equal("gog:65", Assert.Single(GameCatalogRuntime.Rows(state)).Id);
        Assert.Equal(1, state.DiscoveredCount);

        fail = true;
        await coordinator.RequestAsync(manual: true, sourceKey: new CatalogScope(CatalogSource.Gog).Key);
        Assert.Equal(CatalogMatchKind.Game, Match(state, exe));

        fail = false;
        await coordinator.RequestAsync(manual: true, sourceKey: new CatalogScope(CatalogSource.Gog).Key);
        state = state with { Request = state.Request with
        {
            GameRoots = [],
            Overrides = [new(CatalogPaths.FolderId(install), new(Inclusion: CatalogInclusion.Exclude))],
        } };
        await coordinator.SettingsChangedAsync();
        Assert.Equal(CatalogMatchKind.Ignored, Match(state, exe));
    }

    [Fact]
    public async Task Invalid_registry_rows_do_not_open_unc_or_drive_roots()
    {
        string good = Install("Game");
        using var scan = GameCatalogService.CreateScan(new() { DiscoverGog = true, MaxEntries = 1 }, () => [], () => null,
            _ => [new("50", "Good", good), new("51", "Network", @"\\server\share"), new("52", "Root", @"C:\"), new("bad", "Invalid", good)]);
        CatalogResult result;
        do { result = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default); }
        while (scan.HasMore);
        Assert.Equal("gog:50", Assert.Single(result.Entries).Id);
        Assert.Contains(result.Issues, issue => issue.Kind == CatalogIssueKind.InvalidPath);
        Assert.Contains(result.Issues, issue => issue.Kind == CatalogIssueKind.InvalidManifest);
        Assert.False(CatalogClaims.Complete(result));
    }

    private string Install(string name)
    {
        var install = _fixture.CreateSubdirectory(name);
        File.WriteAllBytes(Path.Combine(install.FullName, name + ".exe"), [1]);
        return install.FullName;
    }

    private static CatalogMatchKind Match(GameCatalogSettings state, string path) =>
        new GameCatalogIndex(new(GameCatalogRuntime.Rows(state), [], 0, false, default)).Match(path, true).Kind;

    public void Dispose() => _fixture.Delete(recursive: true);
}
