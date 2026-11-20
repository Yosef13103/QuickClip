using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class GallerySessionsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("qc-sessions-").FullName;
    private static readonly DateTimeOffset First = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Real_session_identity_wins_over_time_gaps_and_game_names()
    {
        Guid session = Guid.NewGuid();
        var a = Clip(1, 0, "Game", "steam:1", session);
        var b = Clip(2, 120, "Renamed", "steam:1", session);
        var c = Clip(3, 121, "Game", "steam:1", Guid.NewGuid());
        var d = Clip(4, 1, "Game", "steam:2", session);
        var e = Clip(5, 2, "Game", null, null);
        ClipItem[] all = [a,b,c,d,e];
        var groups = GallerySessions.Build(all, all, TimeZoneInfo.Utc);
        Assert.Equal(4, groups.Length);
        var actual = Assert.Single(groups, g => g.Clips.Count == 2);
        Assert.Equal([b,a], actual.Clips);
        Assert.Contains("Recorded session", actual.Context);
        Assert.Contains("Approximate session", Assert.Single(groups, g => g.Key.RecordedId == null).Context);
    }

    [Fact]
    public void Legacy_membership_is_built_before_filters_and_separate_from_explicit_sessions()
    {
        var a = Clip(1, 0, "Game");
        var b = Clip(2, 25, "Game");
        var c = Clip(3, 50, "Game");
        var d = Clip(4, 81, "Game");
        var e = Clip(5, 25, "Other");
        var f = Clip(6, 25, "Game", null, Guid.NewGuid());
        ClipItem[] all = [a,b,c,d,e,f];
        var groups = GallerySessions.Build(all, all, TimeZoneInfo.Utc);
        Assert.Equal(4, groups.Length);
        var original = Assert.Single(groups, g => g.Clips.Count == 3);
        var projected = Assert.Single(GallerySessions.Build(all, [a,c], TimeZoneInfo.Utc));
        Assert.Equal(original.Key, projected.Key);
        Assert.Equal([c,a], projected.Clips);
        Assert.Contains("2 clips", projected.Summary);
        Assert.Empty(GallerySessions.Build(all, [], TimeZoneInfo.Utc));
    }

    private ClipItem Clip(int id, int min, string game, string? gameId = null, Guid? session = null)
    {
        string path = Path.Combine(_dir, $"{id}.mp4");
        File.WriteAllText(path, "fixture");
        var date = First.AddMinutes(min);
        return new(new FileInfo(path), new(date, game) { GameId = gameId, RecordingSessionId = session }, new(3,date));
    }
    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
