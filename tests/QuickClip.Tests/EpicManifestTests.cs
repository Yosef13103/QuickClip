using System.Text.Json;
using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class EpicManifestTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-epic-");

    [Theory]
    [InlineData(@"..\Escape.exe")]
    [InlineData(@"C:\Other\Escape.exe")]
    [InlineData(@"\\server\share\Game.exe")]
    [InlineData(@"Bin\..\Game.exe")]
    [InlineData(@"Bin\Game.exe:other")]
    [InlineData("Game.bat")]
    public void Rejects_unsafe_launch_paths(string relative)
    {
        Assert.Null(EpicManifestParser.Parse(Manifest(@"D:\Games\Fixture", relative), out bool malformed));
        Assert.False(malformed);
    }

    [Fact]
    public async Task Manifest_byte_budget_defers_work_without_dropping_any_installs()
    {
        string manifests = _fixture.CreateSubdirectory("many-manifests").FullName;
        const int count = 36;
        for (int i = 0; i < count; i++)
        {
            string install = _fixture.CreateSubdirectory($"Game{i:00}").FullName;
            File.WriteAllBytes(Path.Combine(install, "Play.exe"), [1]);
            byte[] manifest = Manifest(install, "Play.exe", app: $"Fixture{i:00}");
            byte[] padded = new byte[250_000];
            padded.AsSpan().Fill((byte)' ');
            manifest.CopyTo(padded, 0);
            File.WriteAllBytes(Path.Combine(manifests, $"{i:00}.item"), padded);
        }
        using var scan = GameCatalogService.CreateScan(new() { DiscoverEpic = true }, epicManifests: () => manifests);
        var first = await scan.ContinueAsync(1, TimeSpan.FromSeconds(20), default);
        Assert.True(first.InProgress);
        Assert.InRange(first.Entries.Length, 1, count - 1);
        // Renderer evidence now requires an additional *resumable install scan* after
        // reading the manifest; one batch is not guaranteed to finish all installs.
        CatalogResult final = first;
        int slices = 0;
        while (final.InProgress && slices++ < count + 5)
            final = await scan.ContinueAsync(1, TimeSpan.FromSeconds(20), default);
        Assert.False(final.InProgress);
        Assert.Equal(count, final.Entries.Length);
        Assert.Equal(count, final.VisitedEntries);
        Assert.Empty(final.Issues);
    }

    [Fact]
    public void Parses_supported_local_executable_records_and_ignores_non_game_items()
    {
        var valid = EpicManifestParser.Parse(Manifest(@"E:\Games\Fixture", @"Bin\Fixture.exe"), out _);
        Assert.Equal(@"E:\Games\Fixture\Bin\Fixture.exe", valid?.Executable);
        Assert.Equal("epic:fixtureapp", valid?.Id);
        Assert.Null(EpicManifestParser.Parse(Manifest(@"E:\Games\Fixture", "Game.exe", incomplete: true), out _));
        Assert.Null(EpicManifestParser.Parse(Manifest(@"E:\Games\Fixture", "Game.exe", executable: false), out _));
        Assert.Null(EpicManifestParser.Parse(Manifest(@"E:\Games\Fixture", "Game.exe", main: "AnotherApp"), out _));
        Assert.Null(EpicManifestParser.Parse("{"u8.ToArray(), out bool malformed));
        Assert.True(malformed);
    }

    [Fact]
    public async Task Discovers_only_declared_executable_and_preserves_a_corrupt_source_row_for_review()
    {
        string manifests = _fixture.CreateSubdirectory("Manifests").FullName;
        string install = _fixture.CreateSubdirectory("Install").FullName;
        string bin = Directory.CreateDirectory(Path.Combine(install, "Bin")).FullName;
        string exe = Path.Combine(bin, "Fixture.exe");
        File.WriteAllBytes(exe, [1]);
        File.WriteAllBytes(Path.Combine(bin, "Other.exe"), [1]);
        string item = Path.Combine(manifests, "install.item");
        File.WriteAllBytes(item, Manifest(install, @"Bin\Fixture.exe"));
        File.WriteAllBytes(Path.Combine(manifests, "duplicate.item"), Manifest(install, @"Bin\Fixture.exe"));
        File.WriteAllBytes(Path.Combine(manifests, "ignored.txt"), Manifest(install, @"Bin\Other.exe"));
        var request = new CatalogRequest { DiscoverEpic = true, GameRoots = [new(install)] };
        async Task<CatalogResult> Scan(CatalogRequest value) => await GameCatalogService.CreateScan(value,
            epicManifests: () => manifests).ContinueAsync(20, TimeSpan.FromSeconds(5), default);

        var result = await Scan(request);
        Assert.Equal(manifests, result.EpicManifestDirectory);
        var state = GameCatalogRuntime.WithScan(new() { Request = request }, result);
        var game = Assert.Single(GameCatalogRuntime.Rows(state));
        Assert.Equal((CatalogSource.Epic, "Fixture Title", exe), (game.Source, game.Name, Assert.Single(game.RendererExecutables!.Value)));
        var index = new GameCatalogIndex(new([game], [], 0, false, default));
        var window = new WindowOwner(new IntPtr(1), 2, 3, 4);
        var sibling = new PassiveGameWindow(window with { ProcessId = 99 }, Path.Combine(bin, "Other.exe"), 200);
        var renderer = new PassiveGameWindow(window, exe, 100);
        Assert.Equal(window, GameDetector.ResolveEligibleGame(null, null, _ => true,
            () => [sibling, renderer], index.Match)?.VerifiedTarget?.Owner);
        Assert.Null(GameDetector.ResolveEligibleGame(null, null, _ => true, () => [sibling], index.Match));

        var excluded = request with { ExcludedDirectories = [manifests], GameRoots = [] };
        Assert.Empty((await Scan(excluded)).Entries);
        Assert.Empty(GameCatalogRuntime.Rows(state with { Request = excluded }));
        File.WriteAllText(item, "{");
        File.Delete(Path.Combine(manifests, "duplicate.item"));
        var corrupt = await Scan(request with { GameRoots = [] });
        Assert.Equal(CatalogIssueKind.InvalidManifest, Assert.Single(corrupt.Issues).Kind);
        var retained = GameCatalogRuntime.WithScan(state, corrupt);
        Assert.Equal(game.Id, Assert.Single(retained.OfflineIds));
        Assert.Equal(game.Id, Assert.Single(GameCatalogRuntime.Rows(retained)).Id);
    }

    [Fact]
    public async Task Epic_bootstrap_does_not_capture_its_window_and_can_activate_the_shipping_renderer()
    {
        string manifests = _fixture.CreateSubdirectory("Manifests").FullName;
        string install = _fixture.CreateSubdirectory("Install").FullName;
        string launch = Path.Combine(install, "Project.exe");
        string shipping = Path.Combine(install, "Project", "Binaries", "Win64", "Project-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(shipping)!);
        File.WriteAllBytes(launch, [1]);
        File.WriteAllBytes(shipping, [1]);
        File.WriteAllBytes(Path.Combine(manifests, "install.item"), Manifest(install, "Project.exe"));
        var request = new CatalogRequest { DiscoverEpic = true };
        var scan = await GameCatalogService.CreateScan(request, epicManifests: () => manifests)
            .ContinueAsync(20, TimeSpan.FromSeconds(5), default);
        var rows = GameCatalogRuntime.Rows(new() { Request = request, Snapshot = scan });
        Assert.Equal(shipping, Assert.Single(Assert.Single(rows).RendererExecutables!.Value));
        var index = new GameCatalogIndex(scan with { Entries = rows });
        Assert.Equal(CatalogMatchKind.Launcher, index.Match(launch, true).Kind);
        var owner = new WindowOwner((IntPtr)1, 2, 3, 4);
        var foreground = GameDetector.Observe(owner with { Handle = (IntPtr)2 }, launch, index.Match(launch, true), true, new HashSet<string>());
        var selected = GameDetector.ResolveEligibleGame(null, foreground, _ => true, () => [new(owner, shipping, 100)], index.Match);
        Assert.Equal(owner, selected?.VerifiedTarget?.Owner);

        var capped = await GameCatalogService.CreateScan(request with { MaxDepth = 1 }, epicManifests: () => manifests)
            .ContinueAsync(20, TimeSpan.FromSeconds(5), default);
        Assert.True(capped.LimitReached);
        Assert.Equal(CatalogMatchKind.Candidate, new GameCatalogIndex(capped).Match(launch, true).Kind);

        File.Delete(shipping);
        string unrelated = Path.Combine(install, "Project", "Binaries", "Win64", "Tool-Win64-Shipping.exe");
        File.WriteAllBytes(unrelated, [1]);
        var direct = await GameCatalogService.CreateScan(request, epicManifests: () => manifests)
            .ContinueAsync(20, TimeSpan.FromSeconds(5), default);
        var directIndex = new GameCatalogIndex(direct);
        Assert.Equal(CatalogMatchKind.Game, directIndex.Match(launch, true).Kind);
        Assert.Equal(CatalogMatchKind.Unknown, directIndex.Match(unrelated, true).Kind);
    }

    private static byte[] Manifest(string install, string relative, bool incomplete = false, bool executable = true,
        string? main = null, string app = "FixtureApp") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            AppName = app, DisplayName = "Fixture Title", InstallLocation = install, LaunchExecutable = relative,
            bIsExecutable = executable, bIsApplication = true, bIsIncompleteInstall = incomplete, MainGameAppName = main,
        });

    public void Dispose() => _fixture.Delete(recursive: true);
}
