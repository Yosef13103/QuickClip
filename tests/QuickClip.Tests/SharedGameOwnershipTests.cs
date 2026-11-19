using QuickClip.Discovery;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class SharedGameOwnershipTests
{
    private const string Root = @"D:\Games\steamapps\common\Black Ops II";
    private const string Shared = Root + @"\t6zm.exe";
    private const string Campaign = Root + @"\t6sp.exe";
    private const string Other = @"D:\Games\steamapps\common\Other Game\t6zm.exe";

    private static CatalogEntry Main => new("steam:202970", "Black Ops II", CatalogSource.Steam,
        [Root], [Shared, Campaign], new()) { RendererExecutables = [Shared, Campaign] };
    private static CatalogEntry Zombies => new("steam:212910", "Black Ops II Zombies", CatalogSource.Steam,
        [Root], [Shared], new()) { RendererExecutables = [Shared] };
    private static CatalogEntry Separate => new("steam:other", "Other Game", CatalogSource.Steam,
        [Path.GetDirectoryName(Other)!], [Other], new()) { RendererExecutables = [Other] };
    private static CatalogScope Scope => new(CatalogSource.Steam);
    private static CatalogResult Claims => new([Main, Zombies, Separate], [], 0, false, default);
    private static GameCatalogSettings State() => CatalogClaims.Apply(new() { DiscoverSteam = true }, Scope, Claims, false);
    private static GameCatalogIndex Index(GameCatalogSettings state) =>
        new(new(GameCatalogRuntime.Rows(state), [], 0, false, default), CatalogClaims.BlockedExecutables(state));

    [Fact]
    public void Partial_overlap_is_visible_and_owner_choice_survives_refresh_without_merging_other_files()
    {
        var state = State();
        var entries = GameCatalogRuntime.Rows(state);
        var index = Index(state);
        Assert.Equal(3, entries.Length);
        Assert.Equal(CatalogMatchKind.Ambiguous, index.Match(Shared, true).Kind);
        Assert.Equal(new[] { Shared }, SharedGameOwnership.Conflicts(entries.Single(e => e.Id == Main.Id), index));
        Assert.Equal(2, SharedGameOwnership.Claimants(entries, Shared).Length);
        var row = new GamesSettingsView.GameRow(entries.Single(e => e.Id == Main.Id));
        row.Update(row.Entry, false, [], index);
        row.EvaluateIssue([], false);
        Assert.True(row.DetectionReady);
        Assert.Equal(GamesSettingsView.DetectionIssue.OwnershipConflict, row.Issue);
        Assert.False(row.IssueIsBlocking);

        var updated = Assert.IsType<GameCatalogSettings>(SharedGameOwnership.Choose(state, entries, Shared, Main.Id));
        updated = AppSettings.Parse(new AppSettings { Games = updated }.ToJson()).Games;
        updated = CatalogClaims.Apply(updated, Scope, Claims, false);
        var resolved = Index(updated);
        Assert.Equal(CatalogMatchKind.Game, resolved.Match(Shared, true).Kind);
        Assert.Equal(Main.Id, resolved.Match(Shared, true).Entry?.Id);
        Assert.Equal(Main.Id, resolved.Match(Campaign, true).Entry?.Id);
        Assert.Equal(Separate.Id, resolved.Match(Other, true).Entry?.Id);
        Assert.Empty(SharedGameOwnership.Conflicts(Main, resolved));
        Assert.Equal(3, GameCatalogRuntime.Rows(updated).Length);
    }

    [Fact]
    public void Existing_conflicting_attachments_can_be_resolved_without_losing_unrelated_rules()
    {
        var state = State();
        state = state with { Request = state.Request with
        {
            Overrides = [new(Main.Id, new(DisplayName: "Main") { AttachedExecutables = [Shared, Campaign] }),
                new(Zombies.Id, new(DisplayName: "Zombies") { AttachedExecutables = [Shared] })]
        } };
        var entries = GameCatalogRuntime.Rows(state);
        Assert.Equal(CatalogMatchKind.Ambiguous, Index(state).Match(Shared, true).Kind);
        var updated = Assert.IsType<GameCatalogSettings>(SharedGameOwnership.Choose(state, entries, Shared, Zombies.Id));
        var match = Index(updated).Match(Shared, true);
        Assert.Equal(Zombies.Id, match.Entry?.Id);
        Assert.Equal("Zombies", match.Entry?.DisplayName);
        Assert.Equal("Main", Index(updated).Match(Campaign, true).Entry?.DisplayName);
        Assert.Contains(Campaign, updated.Request.Overrides.Single(o => o.Id == Main.Id).Rules.AttachedExecutables);
    }

    [Fact]
    public void Explicit_blocks_and_offline_owners_cannot_be_overridden_by_owner_choice()
    {
        var state = State();
        Assert.Null(SharedGameOwnership.Choose(state with { BlockedExecutables = [Shared] },
            GameCatalogRuntime.Rows(state), Shared, Main.Id));
        var denied = state with { Request = state.Request with { Overrides = [new(Zombies.Id, new(Inclusion: CatalogInclusion.Exclude))] } };
        Assert.Null(SharedGameOwnership.Choose(denied, GameCatalogRuntime.Rows(denied), Shared, Main.Id));
        Assert.Null(SharedGameOwnership.Choose(state, GameCatalogRuntime.Rows(state), Shared, Separate.Id));
        var unavailable = GameCatalogRuntime.Rows(state).Select(e => e.Id == Main.Id
            ? e with { IsUnverified = true, UnverifiedExecutables = [Shared] } : e).ToArray();
        Assert.Null(SharedGameOwnership.Choose(state, unavailable, Shared, Main.Id));
    }

    [Fact]
    public void Stock_mw3_server_is_not_an_automatic_game_but_explicit_attachment_remains_possible()
    {
        const string root = @"D:\Games\steamapps\common\Call of Duty Modern Warfare 3";
        const string server = root + @"\iw5mp_server.exe";
        const string client = root + @"\iw5mp.exe";
        var mp = new CatalogEntry("steam:42690", "Multiplayer", CatalogSource.Steam, [root], [server, client], new())
            { RendererExecutables = [server, client] };
        var dedicated = new CatalogEntry("steam:42750", "Dedicated Server", CatalogSource.Steam, [root], [server], new())
            { RendererExecutables = [server] };
        var index = new GameCatalogIndex(new([mp, dedicated], [], 0, false, default));
        Assert.Equal(CatalogMatchKind.Application, index.Match(server, true).Kind);
        Assert.Equal(CatalogMatchKind.Game, index.Match(client, true).Kind);
        var explicitIndex = new GameCatalogIndex(new([mp with { Rules = new() { AttachedExecutables = [server] } }, dedicated], [], 0, false, default));
        Assert.Equal(CatalogMatchKind.Game, explicitIndex.Match(server, true).Kind);
    }
}