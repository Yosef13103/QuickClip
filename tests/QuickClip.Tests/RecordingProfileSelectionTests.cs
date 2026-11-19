using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class RecordingProfileSelectionTests
{
    private static CatalogEntry Game(string id, string name) =>
        new(id, name, CatalogSource.Steam, [], [], new());

    [Fact]
    public void All_games_first_then_customized_alphabetically_separator_then_inherited()
    {
        var entries = new[] { Game("steam:1", "Zebra"), Game("steam:2", "Alpha"),
            Game("steam:3", "Beta"), Game("steam:4", "Delta") };
        var document = new GameProfileDocument
        {
            Profiles = new GameProfileDocument().Profiles
                .Add("steam:1", new() { Fps = 30 }).Add("steam:3", new() { Aspect = "16:9" }),
        };
        var rows = RecordingProfileSelection.Build(entries, document);
        Assert.Equal(new[] { "All Games", "Beta", "Zebra", "────────", "Alpha", "Delta" },
            rows.Select(row => row.Label));
        Assert.False(rows[3].Enabled);
        Assert.True(rows[1].HasOverrides);
        Assert.False(rows[4].HasOverrides);
        Assert.Single(RecordingProfileSelection.Build([], document));
    }

    [Fact]
    public void Alias_keeps_saved_policy_and_renamed_game_and_reset_preserves_other_profiles()
    {
        var game = Game("steam:220", "New name") with { AliasIds = ["steam:380"] };
        var policy = new GameRecordingPolicy { Aspect = "16:9", Fps = 30 };
        var orphan = new GameRecordingPolicy { Quality = RecordingQuality.High };
        var doc = new GameProfileDocument
        {
            Profiles = new GameProfileDocument().Profiles.Add("steam:380", policy).Add("steam:offline", orphan),
        };
        var row = RecordingProfileSelection.Build([game], doc)[1];
        Assert.Equal("steam:380", row.ProfileKey);
        Assert.True(row.HasOverrides);
        var changed = RecordingProfileSelection.Change(doc, row, policy with { Fps = null });
        var effective = changed.Resolve(row.ProfileKey, new() { Fps = 60, Aspect = "21:9" });
        Assert.Equal(60, effective.Fps);
        Assert.Equal("16:9", effective.Aspect);
        Assert.Throws<InvalidOperationException>(() => RecordingProfileSelection.Change(changed, row, new()));
        var reset = RecordingProfileSelection.Change(changed, RecordingProfileSelection.Build([game], changed)[1], new());
        Assert.False(reset.Profiles.ContainsKey("steam:380"));
        Assert.Equal(orphan, reset.Profiles["steam:offline"]);
    }

    [Fact]
    public void Colliding_aliases_and_shared_profile_keys_require_resolution_and_names_are_disambiguated()
    {
        var a = Game("steam:1", "Same") with { AliasIds = ["steam:3"] };
        var b = Game("steam:2", "Same");
        var doc = new GameProfileDocument
        {
            Profiles = new GameProfileDocument().Profiles.Add(a.Id, new() { Fps = 30 }).Add("steam:3", new() { Fps = 60 }),
        };
        var rows = RecordingProfileSelection.Build([a, b], doc);
        var conflicted = rows.Single(row => row.GameId == a.Id);
        Assert.True(conflicted.Conflict);
        Assert.False(conflicted.Enabled);
        Assert.Contains(a.Id, conflicted.Label);
        Assert.Throws<ArgumentException>(() => RecordingProfileSelection.Change(doc, conflicted, new()));
        var shared = RecordingProfileSelection.Build([
            a with { Rules = new(ProfileId: "shared"), AliasIds = [] },
            b with { Rules = new(ProfileId: "shared") }], new());
        Assert.All(shared.Where(r => r.Kind == RecordingProfileRowKind.Game), row => Assert.True(row.Conflict));
    }
}
