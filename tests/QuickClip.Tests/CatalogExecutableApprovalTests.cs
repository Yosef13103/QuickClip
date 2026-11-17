using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class CatalogExecutableApprovalTests
{
    private const string SteamRoot = @"C:\Games";
    private const string Install = SteamRoot + @"\steamapps\common\Synthetic";
    private const string Selected = Install + @"\osu!.exe";
    private const string Sibling = Install + @"\other.exe";
    private const string Launcher = Install + @"\start.exe";
    private const string Helper = Install + @"\chrome.exe";

    [Fact]
    public void Saved_exact_executable_approvals_still_apply_to_verified_paths()
    {
        var entry = Entry() with { Rules = new() { ApprovedExecutables = [Selected] } };
        var catalog = Index(entry);
        Assert.Equal(CatalogMatchKind.Game, catalog.Match(Selected, true).Kind);
        Assert.Equal(CatalogMatchKind.Candidate, catalog.Match(Sibling, true).Kind);
        Assert.Equal(CatalogMatchKind.Launcher, catalog.Match(Launcher, true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, Index(entry, [Selected]).Match(Selected, true).Kind);
        Assert.Equal(CatalogMatchKind.Inactive, Index(entry with { UnverifiedExecutables = [Selected] }).Match(Selected, true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, Index(entry with
        {
            Rules = entry.Rules with { Inclusion = CatalogInclusion.Exclude },
        }).Match(Selected, true).Kind);
    }

    [Theory]
    [InlineData(" Microsoft® Windows® Operating System ", "UE4Game", "osu!")]
    [InlineData("Unity", "osu!lazer", "osu!lazer")]
    public void Game_name_uses_usable_metadata_or_exact_executable_stem(string product, string description, string expected) =>
        Assert.Equal(expected, ManualGameInput.FriendlyName(Selected, product, description));

    private static GameCatalogIndex Index(CatalogEntry entry, IEnumerable<string>? blocked = null) =>
        new(new([entry], [], 0, false, default), blocked);

    private static CatalogEntry Entry() =>
        new("steam:fixture", "Synthetic game", CatalogSource.Steam, [Install], [Selected, Sibling, Launcher, Helper], new())
        {
            RendererExecutables = [],
            DetectedLaunchers = [Launcher],
        };
}
