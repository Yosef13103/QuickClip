using QuickClip.Discovery;
using QuickClip.Recording;
using QuickClip.Views;

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
    public void Discovered_steam_entry_can_approve_a_selected_rendererless_executable()
    {
        var entry = Entry();
        var row = new GamesSettingsView.GameRow(entry);

        var rules = Assert.IsType<CatalogRules>(row.RulesAfterApproval([Selected]));
        Assert.Equal(Selected, Assert.Single(rules.ApprovedExecutables));
        var catalog = Index(entry with { Rules = rules });
        Assert.Equal(CatalogMatchKind.Game, catalog.Match(Selected, true).Kind);
        Assert.Equal(CatalogMatchKind.Candidate, catalog.Match(Sibling, true).Kind);
        Assert.Null(row.RulesAfterApproval([Launcher]));
        Assert.Null(row.RulesAfterApproval([Helper]));
        Assert.Null(row.RulesAfterApproval([Selected, Helper]));
    }

    [Fact]
    public void Approval_rejects_blocked_offline_ambiguous_and_disabled_entries()
    {
        var entry = Entry();
        var row = new GamesSettingsView.GameRow(entry);
        row.Update(entry, false, [Selected], Index(entry, blocked: [Selected]));
        Assert.Null(row.RulesAfterApproval([Selected]));
        row.Update(entry, true, [], Index(entry));
        Assert.Null(row.RulesAfterApproval([Selected]));
        var duplicate = entry with { Id = "steam:duplicate" };
        row.Update(entry, false, [], new(new([entry, duplicate], [], 0, false, default)));
        Assert.Null(row.RulesAfterApproval([Selected]));
        var disabled = entry with { Rules = new(Inclusion: CatalogInclusion.Exclude) };
        row.Update(disabled, false, [], Index(disabled));
        Assert.Null(row.RulesAfterApproval([Selected]));
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
