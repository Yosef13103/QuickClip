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

    [Fact]
    public void Explicit_non_game_attachment_only_promotes_the_selected_path_and_preserves_blocks()
    {
        const string discord = @"C:\Fixture\Discord\Update.exe";
        const string other = @"D:\Fixture\Discord\Update.exe";
        var entry = Entry() with
        {
            Executables = [discord, other],
            Rules = new() { AttachedExecutables = [discord] },
        };
        var index = Index(entry);
        Assert.Equal(CatalogMatchKind.Game, index.Match(discord, true).Kind);
        Assert.Equal(CatalogMatchKind.Game, QuickClip.Recording.GameDetector.ApplyProcessRole(discord, index.Match(discord, true)).Kind);
        Assert.NotEqual(CatalogMatchKind.Game, index.Match(other, true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, Index(entry, [discord]).Match(discord, true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, Index(entry with
        {
            Rules = entry.Rules with { Inclusion = CatalogInclusion.Exclude },
        }).Match(discord, true).Kind);
        Assert.Equal(CatalogMatchKind.Launcher, Index(entry with
        {
            Rules = entry.Rules with { LauncherExecutables = [discord] },
        }).Match(discord, true).Kind);
        var shared = new GameCatalogIndex(new([entry, entry with { Id = "steam:second" }], [], 0, false, default));
        Assert.Equal(CatalogMatchKind.Ambiguous, shared.Match(discord, true).Kind);
        string protectedExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "notepad.exe");
        Assert.True(CatalogPaths.IsProtectedExecutable(protectedExe));
        Assert.NotEqual(CatalogMatchKind.Game, Index(entry with
        {
            Executables = [protectedExe], Rules = new() { AttachedExecutables = [protectedExe] },
        }).Match(protectedExe, true).Kind);
    }

    [Fact]
    public void Attachment_survives_settings_roundtrip_and_verified_source_refresh()
    {
        const string external = @"D:\Mods\Fixture\Custom.exe";
        var entry = Entry();
        var request = new CatalogRequest
        {
            DiscoverSteam = true,
            Overrides = [new(entry.Id, new() { AttachedExecutables = [external] })],
        };
        var scope = new CatalogScope(CatalogSource.Steam);
        var state = new GameCatalogSettings { Request = request, DiscoverSteam = true };
        var result = new CatalogResult([entry], [], 0, false, default);
        state = CatalogClaims.Apply(state, scope, result, preview: false);
        state = AppSettings.Parse(new AppSettings { Games = state }.ToJson()).Games;
        state = CatalogClaims.Apply(state, scope, result, preview: false);
        var row = Assert.Single(CatalogClaims.Rows(state));
        Assert.Contains(external, row.Executables);
        Assert.Equal(CatalogMatchKind.Game, Index(row).Match(external, true).Kind);
        var denied = state with { Request = state.Request with { ExcludedDirectories = [@"D:\Mods"] } };
        Assert.DoesNotContain(external, Assert.Single(CatalogClaims.Rows(denied)).Executables);
    }

    [Fact]
    public void Shared_source_executable_retains_ambiguity_until_owner_selects_exact_path()
    {
        const string path = @"D:\Games\SteamLibrary\steamapps\common\SharedSource\hl2.exe";
        var first = Entry() with { Id = "steam:220", Executables = [path], RendererExecutables = [path] };
        var second = first with { Id = "steam:380" };
        CatalogMatch Match(CatalogEntry left, CatalogEntry right) =>
            new GameCatalogIndex(new([left, right], [], 0, false, default)).Match(path, true);
        Assert.Equal(CatalogMatchKind.Ambiguous, Match(first, second).Kind);
        Assert.Equal("steam:380", Match(first, second with { Rules = new() { AttachedExecutables = [path] } }).Entry?.Id);
        Assert.Equal(CatalogMatchKind.Ambiguous, Match(
            first with { Rules = new() { AttachedExecutables = [path] } },
            second with { Rules = new() { AttachedExecutables = [path] } }).Kind);
    }

    private static GameCatalogIndex Index(CatalogEntry entry, IEnumerable<string>? blocked = null) =>
        new(new([entry], [], 0, false, default), blocked);

    private static CatalogEntry Entry() =>
        new("steam:fixture", "Synthetic game", CatalogSource.Steam, [Install], [Selected, Sibling, Launcher, Helper], new())
        {
            RendererExecutables = [],
            DetectedLaunchers = [Launcher],
        };
}
