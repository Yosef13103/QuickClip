using System.Text.Json;
using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class GameCatalogTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-catalog-");

    [Fact]
    public async Task Collection_scan_groups_executables_and_respects_exclusions_and_depth()
    {
        string game = FileAt("library", "Game", "Bin", "Game.exe");
        string adjacent = FileAt("library", "Game", "SkipMore", "Helper.EXE");
        FileAt("library", "Game", "Skip", "Private.exe");
        FileAt("library", "Game", "_CommonRedist", "Installer.exe");
        FileAt("library", "Game", "Bin", "Nested", "TooDeep.exe");
        FileAt("library", "Discord", "Update.exe");
        FileAt("library", "Discord", "Discord.exe");
        FileAt("outside", "Outside.exe");
        var request = new CatalogRequest
        {
            GameRoots = [new(At("library"), IsCollection: true)],
            ExcludedDirectories = [At("library", "Game", "Skip")],
            MaxDepth = 2
        };

        var first = await GameCatalogService.ScanAsync(request);
        var candidate = Assert.Single(first.Entries);
        Assert.Equal("Game", candidate.DisplayName);
        Assert.Equal(new[] { game, adjacent }, candidate.Executables);
        Assert.True(first.LimitReached);

        var second = await GameCatalogService.ScanAsync(request with
        {
            Overrides = [new(candidate.Id, new("Renamed", CatalogInclusion.Exclude,
                AppClassification.Companion, CatalogActivation.ForegroundOnly, CatalogCapturePreference.Window, "profile-1", 5))]
        });
        var renamed = Assert.Single(second.Entries);
        Assert.Equal(candidate.Id, renamed.Id);
        Assert.Equal("Renamed", renamed.DisplayName);
        Assert.Equal(CatalogInclusion.Exclude, renamed.Rules.Inclusion);
        Assert.Equal(AppClassification.Companion, renamed.Rules.Classification);
        Assert.Equal("profile-1", renamed.Rules.ProfileId);
        Assert.Equal(5, renamed.Rules.Priority);
    }

    [Fact]
    public async Task An_individual_game_root_groups_its_nested_executables_under_one_identity()
    {
        string direct = FileAt("Game", "Game.exe");
        string nested = FileAt("Game", "bin", "Other.exe");
        var first = await GameCatalogService.ScanAsync(new() { GameRoots = [new(At("Game"))] });
        var second = await GameCatalogService.ScanAsync(new() { GameRoots = [new(At("Game").ToUpperInvariant() + "\\")] });
        Assert.Equal(Assert.Single(first.Entries).Id, Assert.Single(second.Entries).Id);
        Assert.Equal(2, first.Entries[0].Executables.Length);
        Assert.Contains(direct, first.Entries[0].Executables);
        Assert.Contains(nested, first.Entries[0].Executables);
    }

    [Fact]
    public async Task Manual_entries_round_trip_and_can_refer_to_offline_files()
    {
        var id = Guid.NewGuid();
        var request = new CatalogRequest
        {
            ManualEntries = [new(id, "Manual game", [At("Missing.exe"), At("MISSING.EXE")]),
                new(Guid.NewGuid(), "Invalid", ["relative.exe"])]
        };
        request = JsonSerializer.Deserialize<CatalogRequest>(JsonSerializer.Serialize(request))!;
        var first = await GameCatalogService.ScanAsync(request);
        var entry = Assert.Single(first.Entries);
        Assert.Equal(CatalogSource.Manual, entry.Source);
        Assert.Equal(At("Missing.exe"), Assert.Single(entry.Executables));
        Assert.Equal(0, first.VisitedEntries);
        Assert.Equal(CatalogIssueKind.InvalidManualEntry, Assert.Single(first.Issues).Kind);
        var moved = await GameCatalogService.ScanAsync(request with
        {
            ManualEntries = [new(id, "Edited name", [At("Moved.exe")])]
        });
        Assert.Equal(entry.Id, Assert.Single(moved.Entries).Id);
    }

    [Fact]
    public async Task Steam_deduplicates_approved_install_roots_and_keeps_app_identity_across_libraries()
    {
        string firstExe = FileAt("Steam1", "steamapps", "common", "Installed", "Play.exe");
        string secondExe = FileAt("Steam2", "steamapps", "common", "Installed", "Play.exe");
        File.WriteAllText(At("Steam1", "steamapps", "appmanifest_42.acf"), Manifest("42", "Installed"));
        File.WriteAllText(At("Steam2", "steamapps", "appmanifest_42.acf"), Manifest("42", "Installed"));
        File.WriteAllText(At("Steam1", "steamapps", "appmanifest_50.acf"), Manifest("42", "Installed"));
        File.WriteAllText(At("Steam1", "steamapps", "appmanifest_51.acf"), Manifest("51", @"..\Unapproved"));
        File.WriteAllText(At("Steam1", "steamapps", "appmanifest_52.acf"), new string('a', SteamManifestParser.MaxDocumentSize + 1));

        var result = await GameCatalogService.ScanAsync(new()
        {
            SteamLibraryRoots = [At("Steam1"), At("Steam2")],
            GameRoots = [new(At("Steam1", "steamapps", "common", "Installed"))]
        });

        var game = Assert.Single(result.Entries);
        Assert.Equal("steam:42", game.Id);
        Assert.Equal("Steam title", game.DisplayName);
        Assert.Equal(new[] { firstExe, secondExe }, game.Executables);
        Assert.Equal(3, result.Issues.Count(i => i.Kind == CatalogIssueKind.InvalidManifest));
    }

    [Fact]
    public async Task Missing_and_disallowed_roots_do_not_prevent_an_approved_scan()
    {
        FileAt("Game", "Game.exe");
        var result = await GameCatalogService.ScanAsync(new()
        {
            GameRoots = [new(At("Missing")), new(Path.GetPathRoot(_fixture.FullName)!), new(@"\\server\share\games"),
                new(@"\\?\C:\Games"), new("C:relative"), new(At("Game"))]
        });
        Assert.Single(result.Entries);
        Assert.Equal(4, result.Issues.Count(i => i.Kind == CatalogIssueKind.InvalidPath));
        Assert.Contains(result.Issues, issue => issue.Kind == CatalogIssueKind.Unavailable);
    }

    [Fact]
    public async Task Loose_files_do_not_consume_the_candidate_budget()
    {
        for (int i = 0; i < 12; i++) FileAt("Game", $"File{i}.txt");
        var result = await GameCatalogService.ScanAsync(new()
        {
            GameRoots = [new(At("Game"))], MaxEntries = 4
        });
        Assert.Equal(0, result.VisitedEntries);
        Assert.False(result.LimitReached);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public async Task Large_collection_discovers_nested_installs_and_filters_helpers()
    {
        string first = FileAt("library", "Collection", "Game", "Game.exe");
        string second = FileAt("library", "Other", "Game", "Binaries", "Game-Win64-Shipping.exe");
        FileAt("library", "Collection", "Game", "UnityCrashHandler64.exe");
        FileAt("library", "Other", "Game", "Binaries", "unins000.exe");
        for (int i = 0; i < 20_001; i++) File.Create(At("library", $"Loose{i}.txt")).Dispose();
        var result = await GameCatalogService.ScanAsync(new()
        {
            GameRoots = [new(At("library"), IsCollection: true)], MaxEntries = 12,
        });
        Assert.False(result.LimitReached);
        Assert.Equal(2, result.Entries.Select(entry => entry.Id).Distinct().Count());
        Assert.All(result.Entries, entry => Assert.Equal("Game", entry.Name));
        Assert.Equal(new[] { first, second }, result.Entries.SelectMany(entry => entry.Executables).Order());
    }

    [Fact]
    public async Task Steam_config_adds_secondary_library_without_manual_approval_and_honors_exclusions()
    {
        FileAt("Steam", "steamapps", "common", "First", "First.exe");
        FileAt("Other", "steamapps", "common", "Second", "Second.exe");
        File.WriteAllText(At("Steam", "steamapps", "appmanifest_42.acf"), Manifest("42", "First"));
        File.WriteAllText(At("Other", "steamapps", "appmanifest_43.acf"), Manifest("43", "Second"));
        Directory.CreateDirectory(At("Steam", "config"));
        string other = At("Other").Replace("\\", "\\\\");
        File.WriteAllText(At("Steam", "config", "libraryfolders.vdf"),
            $"\"libraryfolders\" {{ \"1\" {{ \"path\" \"{other}\" }} }}");
        var request = new CatalogRequest { DiscoverSteam = true };
        var both = await GameCatalogService.ScanAsync(request, steamInstallations: () => [At("Steam")]);
        Assert.Equal(new[] { "steam:42", "steam:43" }, both.Entries.Select(entry => entry.Id));
        var state = GameCatalogRuntime.WithScan(new(), both);
        Assert.Equal(2, GameCatalogRuntime.Rows(state).Length);
        var excluded = await GameCatalogService.ScanAsync(request with { ExcludedDirectories = [At("Other")] },
            steamInstallations: () => [At("Steam")]);
        Assert.Equal("steam:42", Assert.Single(excluded.Entries).Id);
    }

    [Fact]
    public async Task Cancellation_during_traversal_discards_partial_results()
    {
        FileAt("Game", "Game.exe");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameCatalogService.ScanAsync(
            new() { GameRoots = [new(At("Game"))] }, cancellation.Token, new CancelOnProgress(cancellation)));
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task Invalid_exclusions_fail_closed()
    {
        FileAt("Game", "Game.exe");
        await Assert.ThrowsAsync<ArgumentException>(() => GameCatalogService.ScanAsync(new()
        {
            GameRoots = [new(At("Game"))], ExcludedDirectories = ["relative"]
        }));
    }

    [OptInFact("QUICKCLIP_TEST_DISCOVERY_SYMLINK")]
    public async Task Reparse_points_and_roots_with_reparse_ancestors_are_not_traversed()
    {
        FileAt("outside", "Nested", "Outside.exe");
        string direct = FileAt("Game", "Game.exe");
        string link = At("Game", "Linked");
        Directory.CreateSymbolicLink(link, At("outside"));
        try
        {
            var result = await GameCatalogService.ScanAsync(new()
            {
                GameRoots = [new(At("Game")), new(link, IsCollection: true), new(At("Game", "Linked", "Nested"))]
            });
            Assert.Equal(direct, Assert.Single(Assert.Single(result.Entries).Executables));
            Assert.All(result.Issues, issue => Assert.Equal(CatalogIssueKind.ReparsePoint, issue.Kind));
            Assert.Equal(3, result.Issues.Length);
        }
        finally { Directory.Delete(link); }
    }

    private static string Manifest(string id, string install) =>
        $$""" "AppState" { "appid" "{{id}}" "name" "Steam title" "installdir" "{{install}}" } """;

    private string At(params string[] parts) => Path.Combine([_fixture.FullName, .. parts]);

    private string FileAt(params string[] parts)
    {
        string file = At(parts);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "Synthetic catalog fixture");
        return file;
    }

    public void Dispose() => _fixture.Delete(recursive: true);

    private sealed class CancelOnProgress(CancellationTokenSource cancellation) : IProgress<int>
    {
        public void Report(int value) => cancellation.Cancel();
    }
}
