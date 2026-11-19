using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class GameDefaultTagsTests
{
    private static CatalogEntry Game(string id, string name) =>
        new(id, name, CatalogSource.Steam, [], [], new());

    [Fact]
    public void Tags_are_unique_normalized_and_respect_clip_metadata_limits()
    {
        Assert.Equal(new[] { "Funny", "Café" }, GameDefaultTags.Parse(" Funny \r\nfunny\nCafe\u0301\n").ToArray());
        Assert.Throws<ArgumentException>(() => GameDefaultTags.Parse(new string('x', 33)));
        Assert.Throws<ArgumentException>(() => GameDefaultTags.Parse(string.Join("\n", Enumerable.Range(0, 17).Select(i => $"Tag{i}"))));
        Assert.Empty(GameDefaultTags.Parse("  \n"));
    }

    [Fact]
    public void Only_verified_game_identity_gets_tags_and_alias_conflicts_do_not_guess()
    {
        var main = Game("steam:220", "Half-Life") with { AliasIds = ["steam:380"] };
        var other = Game("steam:400", "Portal");
        var doc = new GameProfileDocument
        {
            Profiles = new GameProfileDocument().Profiles.Add("steam:380", new() { Tags = ["HL2", "Gaming"] }),
        };
        Assert.Equal(new[] { "HL2", "Gaming" }, GameDefaultTags.ForGame(main.Id, false, [main, other], doc));
        Assert.Equal(new[] { "HL2", "Gaming" }, GameDefaultTags.ForGame("steam:380", false, [main, other], doc));
        Assert.Empty(GameDefaultTags.ForGame(other.Id, false, [main, other], doc));
        Assert.Empty(GameDefaultTags.ForGame(main.Id, true, [main, other], doc));
        Assert.Empty(GameDefaultTags.ForGame(null, false, [main, other], doc));
        var conflict = doc with { Profiles = doc.Profiles.Add(main.Id, new() { Tags = ["Other"] }) };
        Assert.Empty(GameDefaultTags.ForGame(main.Id, false, [main, other], conflict));
    }
}
