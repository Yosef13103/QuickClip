using System.Diagnostics;
using QuickClip.Discovery;
using Xunit.Abstractions;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class GameDiscoveryActivationTests(ITestOutputHelper output) : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-activation-");

    [Fact]
    public async Task A_finished_install_can_arm_recording_while_its_collection_is_still_scanning()
    {
        string play = Create("A osu!lazer", "current", "osu!.exe");
        string alternate = Create("A osu!lazer", "app-1.2.3", "Alternate.exe");
        string runtime = Create("A osu!lazer", "current", "javaw.exe");
        string browser = Create("A osu!lazer", "current", "chrome.exe");
        string utility = Create("A osu!lazer", "Extras", "Tool.exe");
        string projectBinary = Create("A osu!lazer", "Project", "Binaries", "Win64", "Game64.exe");
        for (int i = 0; i < 64; i++) Create("Z later game", $"Asset{i:00}.dat");
        for (int i = 0; i < 12; i++) Create("Z later game", $"Game{i}.exe");

        var scope = new CatalogScope(CatalogSource.Folder, _fixture.FullName, IsCollection: true);
        var state = new GameCatalogSettings
        {
            DiscoverSteam = false,
            DiscoverEpic = false,
            Request = new() { GameRoots = [new(_fixture.FullName, true)], MaxEntries = 1 },
        };
        using var scan = GameCatalogService.CreateScan(scope.Request(state.Request));
        var elapsed = Stopwatch.StartNew();
        int slices = 0;
        CatalogResult result;
        do
        {
            slices++;
            result = await scan.ContinueAsync(1, TimeSpan.Zero, default);
        }
        while (result.InProgress && !result.VerifiedInstalls.Any(entry => entry.Executables.Contains(play)));

        Assert.True(result.InProgress);
        Assert.Contains(result.VerifiedInstalls, entry => entry.Executables.Contains(play));
        var preview = CatalogClaims.Apply(state, scope, result, preview: true);
        var cache = new GameCatalogIndexCache();
        var index = cache.Get(preview);
        Assert.Same(index, cache.Get(preview with
        {
            ScanPreview = preview.ScanPreview! with { Result = result with { VisitedEntries = result.VisitedEntries + 1 } },
        }));
        Assert.Null(Observe(index, play, current: false));
        Assert.NotNull(Observe(index, alternate));
        Assert.NotNull(Observe(index, projectBinary));
        Assert.Null(Observe(index, runtime));
        Assert.Null(Observe(index, browser));
        Assert.Null(Observe(index, utility));
        Assert.Null(Observe(index, Path.Combine(_fixture.Parent!.FullName, "osu!.exe")));

        var display = new MonitorInfo("DISPLAY1", "", 0, 0, 1920, 1080) { CaptureIdentity = "fixture-display" };
        CaptureActivation? activation = null;
        var recorder = new RecordingLifecycle(value => { activation = value; return null; }, () => activation = null);
        var observation = Assert.IsType<GameCaptureObservation>(Observe(index, play));
        recorder.Reconcile(false, null, observation, [display], _ => display.DeviceName, _ => true);
        Assert.Null(activation);
        recorder.Reconcile(true, null, observation, [display], _ => display.DeviceName, _ => false);
        Assert.Null(activation);
        recorder.Reconcile(true, null, observation, [display], _ => display.DeviceName, _ => true);
        Assert.Equal(observation.Owner, activation?.GameOwner);
        Assert.Equal(RecordingMode.GameTriggered, recorder.Mode);

        var disabled = preview with { DisabledSourceKeys = [scope.Key] };
        var revoked = Observe(cache.Get(disabled), play);
        Assert.Null(revoked);
        recorder.Reconcile(true, null, revoked, [display], _ => display.DeviceName, _ => true);
        Assert.Null(activation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            scan.ContinueAsync(1, TimeSpan.FromSeconds(1), new CancellationToken(canceled: true)));
        int visitedAtCancel = result.VisitedEntries;
        do
        {
            slices++;
            result = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
        }
        while (result.InProgress);
        Assert.True(result.VisitedEntries > visitedAtCancel);
        Assert.True(result.InspectedEntries >= result.VisitedEntries + 64);
        output.WriteLine($"Continued scan: {slices} slices, {result.InspectedEntries} entries inspected, {elapsed.Elapsed.TotalMilliseconds:F1} ms including fixture assertions.");
        Assert.Contains(result.VerifiedInstalls, entry => entry.Executables.Contains(play));
    }

    [Fact]
    public async Task Verified_install_survives_an_unrelated_incomplete_install()
    {
        string play = Create("Portable game", "current", "Game.exe");
        string uncheckedPath = Create("Deep game", "bin", "bin", "Deep.exe");
        var scope = new CatalogScope(CatalogSource.Folder, _fixture.FullName, IsCollection: true);
        var state = new GameCatalogSettings
        {
            Request = new() { GameRoots = [new(_fixture.FullName, true)], MaxDepth = 2 },
        };
        var result = await GameCatalogService.ScanAsync(scope.Request(state.Request));
        Assert.True(result.LimitReached);
        var cache = new GameCatalogIndexCache();
        var index = cache.Get(CatalogClaims.Apply(state, scope, result, preview: false));
        Assert.NotNull(Observe(index, play));
        Assert.Null(Observe(index, uncheckedPath));
    }

    [Fact]
    public void Exact_selection_overrides_inferred_launcher_but_not_an_explicit_veto()
    {
        string play = Create("osu!lazer", "current", "osu!.exe");
        var install = Path.GetDirectoryName(Path.GetDirectoryName(play)!)!;
        var scope = new CatalogScope(CatalogSource.Folder, install);
        var folder = new CatalogEntry(CatalogPaths.FolderId(install), "Discovered osu!", CatalogSource.Folder,
            [install], [play], new()) { DetectedLaunchers = [play] };
        var manual = new ManualCatalogEntry(Guid.NewGuid(), "Exact osu!", [play]);
        var state = new GameCatalogSettings
        {
            Request = new() { GameRoots = [new(install)], ManualEntries = [manual] },
        };
        var cache = new GameCatalogIndexCache();
        var discovered = CatalogClaims.Apply(state, scope, new([folder], [], 0, false, default), preview: false);
        Assert.Equal("Exact osu!", Observe(cache.Get(discovered), play)?.VerifiedTarget?.Game);

        var launcher = new CatalogOverride(folder.Id, new() { LauncherExecutables = [play] });
        Assert.Equal(CatalogMatchKind.Launcher, cache.Get(discovered with
        {
            Request = state.Request with { Overrides = [launcher] },
        }).Match(play, true).Kind);
        Assert.Null(Observe(cache.Get(discovered with { BlockedExecutables = [play] }), play));
    }

    [Fact]
    public void Cached_aliases_and_escaping_paths_cannot_change_verified_folder_eligibility()
    {
        const string root = @"C:\Approved";
        const string install = root + @"\Install";
        const string path = install + @"\current\Game.exe";
        const string escapedPath = install + @"\..\..\Outside.exe";
        var collection = new CatalogScope(CatalogSource.Folder, root, IsCollection: true);
        var direct = new CatalogScope(CatalogSource.Folder, install + @"\current");
        var broad = new CatalogEntry(CatalogPaths.FolderId(install), "Install", CatalogSource.Folder,
            [install], [path, escapedPath], new()) { RendererExecutables = [path, escapedPath] };
        var narrow = new CatalogEntry(CatalogPaths.FolderId(direct.Root!), "Direct", CatalogSource.Folder,
            [direct.Root!], [path], new()) { RendererExecutables = [path] };
        var cached = broad with
        {
            RendererExecutables = [],
            DetectedLaunchers = [path],
        };
        var state = new GameCatalogSettings
        {
            Request = new() { GameRoots = [new(root, true), new(direct.Root!)] },
        };
        var cachedResult = new CatalogResult([cached], [new(root, CatalogIssueKind.Unavailable)], 0, false, default);
        state = CatalogClaims.Apply(state, collection, cachedResult, preview: false);
        state = CatalogClaims.Apply(state, direct, new([narrow], [], 0, false, default), preview: false);
        var cache = new GameCatalogIndexCache();
        Assert.Equal("Direct", Observe(cache.Get(state), path)?.VerifiedTarget?.Game);
        state = CatalogClaims.Apply(state, collection, new([broad], [], 0, false, default), preview: false);
        var index = cache.Get(state);
        Assert.Equal("Direct", Observe(index, path)?.VerifiedTarget?.Game);
        Assert.Null(Observe(index, escapedPath));
        Assert.Null(Observe(index, @"C:\Outside.exe"));
    }

    [Fact]
    public void Progress_only_publications_reuse_the_index_for_a_large_catalog()
    {
        const int count = 2000;
        const string root = @"C:\SyntheticGames";
        var scope = new CatalogScope(CatalogSource.Folder, root, IsCollection: true);
        var entries = Enumerable.Range(0, count).Select(i =>
        {
            string install = Path.Combine(root, $"Game{i}");
            string executable = Path.Combine(install, "Play.exe");
            return new CatalogEntry(CatalogPaths.FolderId(install), $"Game{i}", CatalogSource.Folder,
                [install], [executable], new()) { RendererExecutables = [executable] };
        }).ToArray();
        var evidence = new CatalogResult([.. entries], [], count, false, default)
        {
            InProgress = true,
            VerifiedInstalls = [.. entries],
        };
        var state = CatalogClaims.Apply(new() { Request = new() { GameRoots = [new(root, true)] } },
            scope, evidence, preview: true);
        _ = new GameCatalogIndexCache().Get(state); // Warm JIT before taking measurements.
        var cache = new GameCatalogIndexCache();
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        long time = Stopwatch.GetTimestamp();
        var index = cache.Get(state);
        long buildBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        double buildMs = Stopwatch.GetElapsedTime(time).TotalMilliseconds;

        bytes = GC.GetAllocatedBytesForCurrentThread();
        time = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            var preview = state.ScanPreview!;
            state = state with { ScanPreview = preview with { Result = preview.Result with { InspectedEntries = count + i } } };
            Assert.Same(index, cache.Get(state));
            if (index.Match(entries[i].Executables[0], true).Kind != CatalogMatchKind.Game)
                Assert.Fail("A verified executable lost its match during scan progress.");
        }
        long matchBytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        double matchMs = Stopwatch.GetElapsedTime(time).TotalMilliseconds;
        output.WriteLine($"Catalog {count}: build {buildMs:F1} ms / {buildBytes:N0} bytes; " +
            $"{count} matches plus progress publications {matchMs:F1} ms / {matchBytes:N0} bytes; zero index rebuilds.");
    }

    private static GameCaptureObservation? Observe(GameCatalogIndex index, string path, bool current = true)
    {
        var owner = new WindowOwner((IntPtr)1, 2, 3, 4);
        var foreground = GameDetector.Observe(owner, path, index.Match(path, true), false, new HashSet<string>());
        return GameDetector.ResolveEligibleGame(null, foreground, _ => current, () => [], index.Match);
    }

    private string Create(params string[] parts)
    {
        string path = Path.Combine(_fixture.FullName, Path.Combine(parts));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    public void Dispose() => _fixture.Delete(recursive: true);
}
