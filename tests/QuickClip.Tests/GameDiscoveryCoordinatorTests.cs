namespace QuickClip.Tests;

using QuickClip.Discovery;

public sealed class GameDiscoveryCoordinatorTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-discovery-");

    [Fact]
    public async Task Pause_inside_an_install_resumes_the_same_cursor_with_no_lost_or_duplicate_entries()
    {
        string install = _fixture.CreateSubdirectory("Many executables").FullName;
        string[] expected = Enumerable.Range(0, 30).Select(i => Path.Combine(install, $"Play{i:00}.exe")).ToArray();
        foreach (string path in expected) File.WriteAllBytes(path, [1]);
        var request = new CatalogRequest { GameRoots = [new(install)], MaxEntries = 3 };
        var lastFinished = DateTimeOffset.UtcNow.AddDays(-1);
        GameCatalogSettings state = new() { Request = request, Snapshot = new([], [], 0, false, lastFinished) };
        int scans = 0;
        using var coordinator = new GameDiscoveryCoordinator(() => state, (updated, _) => state = updated,
            request => { scans++; return GameCatalogService.CreateScan(request, () => [], () => null); });
        bool pauseOnce = true;
        coordinator.Changed += () =>
        {
            if (pauseOnce && state.Snapshot?.VisitedEntries > 0)
            {
                pauseOnce = false;
                coordinator.Pause();
            }
        };

        await coordinator.RequestAsync(manual: true);
        Assert.True(coordinator.IsPaused);
        Assert.StartsWith("Paused", coordinator.Status);
        Assert.Equal(lastFinished, state.Snapshot!.CompletedAt);
        Assert.InRange(state.Snapshot.VisitedEntries, 1, expected.Length - 1);
        await coordinator.RequestAsync(manual: true);
        Assert.False(coordinator.IsPaused);
        Assert.StartsWith("Completed", coordinator.Status);
        Assert.Equal(1, scans);
        Assert.Equal(expected.Length, state.Snapshot!.VisitedEntries);
        Assert.Equal(expected, Assert.Single(state.Snapshot.Entries).Executables.Order(StringComparer.OrdinalIgnoreCase));
        Assert.True(state.Snapshot.CompletedAt > lastFinished);
    }

    [Fact]
    public async Task Disposing_a_paused_scan_releases_directory_cursors()
    {
        string root = AddInstall("Game");
        using var scan = GameCatalogService.CreateScan(new() { GameRoots = [new(root)], MaxEntries = 1 });
        await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
        Assert.True(scan.HasMore);
        scan.Dispose();
        Assert.False(scan.HasMore);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default));
        Directory.Delete(root, recursive: true);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Startup_without_a_view_coalesces_manual_and_automatic_requests()
    {
        string root = AddInstall("Game");
        GameCatalogSettings state = new() { Request = new() { GameRoots = [new(root)] } };
        var published = new TaskCompletionSource<GameCatalogSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
        int scans = 0;
        using var coordinator = new GameDiscoveryCoordinator(() => state, (updated, persist) =>
        {
            state = updated;
            if (persist) published.TrySetResult(updated);
        }, request =>
        {
            scans++;
            return GameCatalogService.CreateScan(request, () => [], () => null);
        });
        coordinator.Start();
        var running = coordinator.RequestAsync();
        Assert.Same(running, coordinator.RequestAsync(manual: true));
        var finished = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await running;
        await coordinator.RequestAsync();
        Assert.Equal(1, scans);
        Assert.Equal("Game", Assert.Single(finished.Snapshot!.Entries).Name);
    }

    [Fact]
    public async Task Pause_resumes_at_an_install_boundary_without_revisiting_completed_sources()
    {
        string first = AddInstall("First");
        string second = AddInstall("Second");
        GameCatalogSettings state = new() { Request = new() { GameRoots = [new(first), new(second)] } };
        int scans = 0;
        using var coordinator = new GameDiscoveryCoordinator(() => state, (updated, _) => state = updated, request =>
        {
            scans++;
            return GameCatalogService.CreateScan(request, () => [], () => null);
        }, segmentsPerSlice: 1);
        bool pause = true;
        coordinator.Changed += () =>
        {
            if (pause && state.Snapshot?.Entries.Length == 1)
            {
                pause = false;
                coordinator.Pause();
            }
        };

        await coordinator.RequestAsync(manual: true);
        Assert.Equal("First", Assert.Single(state.Snapshot!.Entries).Name);
        Assert.Contains("Paused", coordinator.Status);
        Directory.Move(first, first + "-gone");
        await coordinator.RequestAsync(manual: true);
        Assert.Equal(1, scans);
        Assert.Equal(new[] { "First", "Second" }, state.Snapshot!.Entries.Select(entry => entry.Name));
        Assert.Equal(2, state.Snapshot.VisitedEntries);
        Assert.False(state.Snapshot.LimitReached);
        Assert.Empty(state.Snapshot.Issues);
    }

    [Fact]
    public async Task Steam_install_checkpoint_does_not_reread_the_library_or_reprobe_a_finished_install()
    {
        string steam = _fixture.CreateSubdirectory("Steam").FullName;
        string apps = Directory.CreateDirectory(Path.Combine(steam, "steamapps")).FullName;
        for (int id = 1; id <= 2; id++)
        {
            string game = $"Game{id}";
            string install = Directory.CreateDirectory(Path.Combine(apps, "common", game)).FullName;
            File.WriteAllBytes(Path.Combine(install, game + ".exe"), [1]);
            File.WriteAllText(Path.Combine(apps, $"appmanifest_{id}.acf"),
                $$""" "AppState" { "appid" "{{id}}" "name" "{{game}}" "installdir" "{{game}}" } """);
        }
        var scan = GameCatalogService.CreateScan(new() { SteamLibraryRoots = [steam] });
        var manifestBatch = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
        Assert.All(manifestBatch.Entries, entry => Assert.Empty(entry.Executables));
        var first = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
        var completed = Assert.Single(first.Entries.Where(entry => !entry.Executables.IsEmpty));
        Directory.Move(completed.InstallDirectories[0], completed.InstallDirectories[0] + "-gone");
        File.Delete(Path.Combine(apps, "appmanifest_1.acf"));
        File.Delete(Path.Combine(apps, "appmanifest_2.acf"));
        var final = await scan.ContinueAsync(1, TimeSpan.FromSeconds(1), default);
        Assert.False(scan.HasMore);
        Assert.All(final.Entries, entry => Assert.Single(entry.Executables));
        Assert.Empty(final.Issues);
        Assert.Equal(4, final.VisitedEntries);
    }

    [Fact]
    public async Task Persisted_opt_out_stops_startup_and_mid_scan_opt_out_preserves_snapshot()
    {
        string root = AddInstall("Game");
        var snapshot = new CatalogResult([], [], 7, false, default);
        var state = new GameCatalogSettings { AutoScan = false, Snapshot = snapshot };
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int scans = 0, publications = 0;
        using var coordinator = new GameDiscoveryCoordinator(() => state, (updated, _) =>
        {
            publications++;
            state = updated;
        }, request =>
        {
            scans++;
            return GameCatalogService.CreateScan(request, () =>
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return [root];
            }, () => null);
        });
        coordinator.Start();
        await coordinator.RequestAsync();
        Assert.Equal(0, scans);
        state = state with { AutoScan = true };
        var enabling = coordinator.SettingsChangedAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        state = state with { AutoScan = false };
        var disabling = coordinator.SettingsChangedAsync();
        release.Set();
        await Task.WhenAll(enabling, disabling);
        Assert.Equal(1, scans);
        Assert.Equal(0, publications);
        Assert.Same(snapshot, state.Snapshot);
        Assert.False(coordinator.IsRunning);
    }

    private string AddInstall(string name)
    {
        string path = _fixture.CreateSubdirectory(name).FullName;
        File.WriteAllBytes(Path.Combine(path, name + ".exe"), [1, 2, 3]);
        return path;
    }

    public void Dispose() => _fixture.Delete(recursive: true);
}
