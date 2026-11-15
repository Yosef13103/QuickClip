using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class CatalogSourceMutationTests
{
    [Fact]
    public async Task Adding_a_root_does_not_rescan_the_already_verified_root()
    {
        var fixture = Directory.CreateTempSubdirectory("quickclip-source-mutation-");
        try
        {
            string first = fixture.CreateSubdirectory("First").FullName;
            string second = fixture.CreateSubdirectory("Second").FullName;
            File.WriteAllBytes(Path.Combine(first, "First.exe"), [1]);
            File.WriteAllBytes(Path.Combine(second, "Second.exe"), [1]);
            GameCatalogSettings state = new() { DiscoverSteam = false, DiscoverEpic = false, Request = new() { GameRoots = [new(first)] } };
            var visited = new List<string[]>();
            using var coordinator = new GameDiscoveryCoordinator(() => state, (updated, _) => state = updated,
                request =>
                {
                    visited.Add(request.GameRoots.Select(root => Path.GetFileName(root.Path)).ToArray());
                    return GameCatalogService.CreateScan(request, () => [], () => null);
                });
            coordinator.Start();
            await coordinator.RequestAsync();
            string firstId = Assert.Single(state.Snapshot!.Entries).Id;

            state = state with { Request = state.Request with { GameRoots = state.Request.GameRoots.Add(new(second)) } };
            await coordinator.SettingsChangedAsync();

            Assert.Equal(2, visited.Count);
            Assert.Equal(new[] { "First" }, visited[0]);
            Assert.Equal(new[] { "Second" }, visited[1]);
            Assert.Contains(state.Snapshot!.Entries, entry => entry.Id == firstId);
            Assert.Equal(2, GameCatalogRuntime.Rows(state).Length);
            Assert.Equal(1, state.DiscoveredCount);
            state = state with { Request = state.Request with { Overrides = [new(firstId, new("Owner title"))] } };
            await coordinator.SettingsChangedAsync();
            Assert.Equal(2, visited.Count);
            Assert.Contains(GameCatalogRuntime.Rows(state), entry => entry.DisplayName == "Owner title");
        }
        finally
        {
            fixture.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Removing_a_source_mid_scan_rejects_its_completion_and_runs_the_new_source()
    {
        using var fixture = new Scratch();
        string steam = fixture.Install("Steam");
        string folder = fixture.Install("Folder");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        GameCatalogSettings state = new() { DiscoverSteam = true, DiscoverEpic = false };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return [steam];
            }, () => null));
        coordinator.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state = state with { DiscoverSteam = false, Request = new() { GameRoots = [new(folder)] } };
        var changed = coordinator.SettingsChangedAsync();
        release.Set();
        await changed.WaitAsync(TimeSpan.FromSeconds(10));
        var claim = Assert.Single(state.SourceClaims!.Value);
        Assert.Equal(CatalogSource.Folder, claim.Scope.Kind);
        Assert.Equal(folder, claim.Scope.Root);
        Assert.Empty(state.Snapshot!.SteamLibraries);
    }

    [Fact]
    public async Task Adding_an_unrelated_root_keeps_an_in_progress_provider_cursor()
    {
        using var fixture = new Scratch();
        string install = fixture.Install("GogGame");
        string folder = fixture.Install("Folder");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int providerReads = 0;
        GameCatalogSettings state = new() { AutoScan = false, DiscoverGog = true };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next,
            request => GameCatalogService.CreateScan(request, () => [], () => null, _ =>
            {
                providerReads++;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return [new("89", "GOG Game", install)];
            }));
        coordinator.Start();
        var scanning = coordinator.RequestAsync(manual: true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state = state with { Request = new() { GameRoots = [new(folder)] } };
        var changed = coordinator.SettingsChangedAsync();
        release.Set();
        await Task.WhenAll(scanning, changed).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, providerReads);
        Assert.Equal(2, state.SourceClaims!.Value.Length);
        Assert.Equal(2, GameCatalogRuntime.Rows(state).Length);
    }

    [Fact]
    public async Task Overlapping_grants_deduplicate_and_revoke_independently()
    {
        using var fixture = new Scratch();
        string collection = fixture.Install("Library", createExecutable: false);
        string install = fixture.Install(@"Library\Game");
        string play = Path.Combine(install, "Game.exe");
        GameCatalogSettings state = new() { AutoScan = false, Request = new() { GameRoots = [new(collection, true), new(install)] } };
        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next);
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(2, state.SourceClaims!.Value.Length);
        var row = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal(CatalogMatchKind.Game, Match(state, play).Kind);
        var owner = new WindowOwner((IntPtr)1, 2, 3, 4);
        var observed = GameDetector.Observe(owner, play, Match(state, play), false, new HashSet<string>());
        Assert.NotNull(observed.VerifiedTarget);
        bool running = false;
        var display = new MonitorInfo("DISPLAY1", "", 0, 0, 1920, 1080) { CaptureIdentity = "fixture-display" };
        var recorder = new RecordingLifecycle(_ => { running = true; return null; }, () => running = false);
        recorder.Reconcile(true, null, observed, [display], _ => display.DeviceName, _ => true);
        Assert.True(running);
        state = state with { Request = state.Request with { GameRoots = [new(install)] } };
        await coordinator.SettingsChangedAsync();
        Assert.Equal(row.Id, Assert.Single(GameCatalogRuntime.Rows(state)).Id);
        Assert.Equal(CatalogMatchKind.Game, Match(state, play).Kind);

        string key = Assert.Single(CatalogScope.Enabled(state)).Key;
        state = state with { DisabledSourceKeys = [key] };
        await coordinator.SettingsChangedAsync();
        Assert.Empty(GameCatalogRuntime.Rows(state));
        var eligible = CaptureSourcePolicy.RefreshEligibleGame(observed, _ => true, (path, _) => Match(state, path));
        Assert.Null(eligible);
        recorder.Reconcile(true, null, eligible, [display], _ => display.DeviceName, _ => true);
        Assert.False(running);
    }

    [Fact]
    public async Task Legacy_and_unavailable_cache_keep_ids_and_rules_without_granting_capture()
    {
        using var fixture = new Scratch();
        string install = fixture.Install("Game");
        string play = Path.Combine(install, "Game.exe");
        var request = new CatalogRequest { GameRoots = [new(install)] };
        var legacy = await GameCatalogService.ScanAsync(request);
        string id = Assert.Single(legacy.Entries).Id;
        request = request with { Overrides = [new(id, new("Owner title"))] };
        var state = CatalogClaims.Start(new() { AutoScan = false, Request = request, Snapshot = legacy });
        Assert.Empty(state.SourceClaims!.Value);
        Assert.Equal(CatalogMatchKind.Inactive, Match(state, play).Kind);

        using var coordinator = new GameDiscoveryCoordinator(() => state, (next, _) => state = next);
        await coordinator.RequestAsync(manual: true);
        Assert.Empty(state.LegacyEntries);
        Assert.Equal(CatalogMatchKind.Game, Match(state, play).Kind);
        Directory.Move(install, install + "-offline");
        await coordinator.RequestAsync(manual: true);
        state = AppSettings.Parse(new AppSettings { Games = state }.ToJson()).Games;
        var cached = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal(id, cached.Id);
        Assert.Equal("Owner title", cached.DisplayName);
        Assert.Equal(CatalogMatchKind.Inactive, Match(state, play).Kind);
        Assert.Equal(0, state.DiscoveredCount);

        Directory.CreateDirectory(install);
        await coordinator.RequestAsync(manual: true);
        Assert.Empty(GameCatalogRuntime.Rows(state));
        Assert.Equal(id, Assert.Single(state.Request.Overrides).Id);
    }

    private static CatalogMatch Match(GameCatalogSettings settings, string path) =>
        new GameCatalogIndex(new(GameCatalogRuntime.Rows(settings), [], 0, false, default)).Match(path, true);

    private sealed class Scratch : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("quickclip-source-");

        public string Install(string name, bool createExecutable = true)
        {
            var directory = _root.CreateSubdirectory(name);
            if (createExecutable) File.WriteAllBytes(Path.Combine(directory.FullName, directory.Name + ".exe"), [1]);
            return directory.FullName;
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
