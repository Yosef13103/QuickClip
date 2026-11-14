using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class GameDiscoveryCountTests
{
    [Fact]
    public void Discovery_progress_excludes_manual_and_retained_games()
    {
        var retained = new CatalogEntry("folder:retained", "Retained", CatalogSource.Folder,
            [@"C:\Games\Retained"], [@"C:\Games\Retained\Play.exe"], new());
        var discovered = new CatalogEntry("folder:found", "Found", CatalogSource.Folder,
            [@"C:\Games\Found"], [@"C:\Games\Found\Play.exe"], new());
        var manual = new CatalogEntry("manual:fixture", "Manual", CatalogSource.Manual, [],
            [@"C:\Manual\Play.exe"], new());
        var prior = new GameCatalogSettings { Snapshot = new([retained], [], 2, false, default) };
        var scan = new CatalogResult([discovered, manual], [], 15, false, default) { InProgress = true };
        scan = scan with { CandidateExecutablesVisited = 4 };
        var state = GameCatalogRuntime.WithScan(prior, scan);

        Assert.Equal(retained.Id, Assert.Single(state.RetainedIds));
        Assert.Equal("Scanning · 4 candidate executable visits · 1 discovered this scan",
            GameDiscoveryCoordinator.Describe(state, "Scanning", scan));
    }

    [Fact]
    public async Task Candidate_visits_exclude_folders_helpers_and_manual_entries()
    {
        var scratch = Directory.CreateTempSubdirectory("quickclip-discovery-counts-");
        try
        {
            var install = scratch.CreateSubdirectory("TestGame");
            File.WriteAllBytes(Path.Combine(install.FullName, "TestGame.exe"), [0]);
            File.WriteAllBytes(Path.Combine(install.FullName, "Setup.exe"), [0]);
            File.WriteAllText(Path.Combine(install.FullName, "readme.txt"), "");
            var request = new CatalogRequest
            {
                GameRoots = [new(scratch.FullName, IsCollection: true)],
                ManualEntries = [new(Guid.NewGuid(), "Manual game", [@"C:\Manual\Play.exe"])],
            };
            using var scan = GameCatalogService.CreateScan(request);
            CatalogResult result;
            do { result = await scan.ContinueAsync(2, TimeSpan.FromSeconds(1), default); }
            while (result.InProgress);

            Assert.True(result.VisitedEntries > 1);
            Assert.Equal(1, result.CandidateExecutablesVisited);
            Assert.Equal(2, result.Entries.Length);
        }
        finally { scratch.Delete(recursive: true); }
    }
}
