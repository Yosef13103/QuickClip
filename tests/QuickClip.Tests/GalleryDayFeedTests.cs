using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class GalleryDayFeedTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("qc-day-feed-").FullName;
    private static readonly DateTimeOffset First = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Groups_by_local_midnight_across_source_offsets_and_uses_corrected_games()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture+03", TimeSpan.FromHours(3), "fixture", "fixture");
        var first = Clip(1, new DateTimeOffset(2026, 11, 11, 23, 59, 0, TimeSpan.FromHours(3)), "Original");
        first.ApplyDetails(first.Details with { Game = "Renamed game" });
        var sameDay = Clip(2, new DateTimeOffset(2026, 11, 12, 1, 55, 0, TimeSpan.FromHours(5)), "Another game");
        var other = Clip(3, new DateTimeOffset(2026, 11, 11, 21, 1, 0, TimeSpan.Zero), "Renamed game");
        var rows = GalleryDayFeed.Build([first, sameDay, other], 4, zone).OfType<GalleryDayHeader>().ToArray();
        Assert.Equal(new DateTime(2026, 11, 12), rows[0].Day);
        Assert.Equal(new DateTime(2026, 11, 11), rows[1].Day);
        Assert.Equal("Renamed game, Another game · 2 clips", rows[1].Summary);
    }

    [Fact]
    public void Day_selection_supports_mixed_state_and_keeps_other_days_and_filtered_clips()
    {
        var clips = Enumerable.Range(0, 5).Select(i => Clip(i, i < 3 ? First : First.AddDays(-1), "Game")).ToArray();
        var days = GalleryDayFeed.Build(clips, 2, TimeZoneInfo.Utc).OfType<GalleryDayHeader>().ToArray();
        clips[0].IsSelected = true;
        days[0].RefreshSelection();
        Assert.Null(days[0].Selection);
        foreach (var clip in days.SelectMany(day => day.Clips)) clip.IsSelected = true;
        foreach (var day in days) day.RefreshSelection();
        Assert.All(days, day => Assert.Equal(true, day.Selection));
        Assert.Equal(5, GalleryDayFeed.SelectedPaths(clips).Count);
        foreach (var clip in days[0].Clips) clip.IsSelected = false;
        days[0].RefreshSelection();
        Assert.Equal(false, days[0].Selection);
        Assert.Equal(true, days[1].Selection);
        Assert.Equal(2, GalleryDayFeed.SelectedPaths(clips).Count);
        Assert.Single(GalleryDayFeed.SelectedPaths([clips[4]]));
        Assert.True(clips[3].IsSelected); // Hidden by the filter, but retained for when it is cleared.
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(50, 4)]
    public void Flattens_one_thousand_clips_into_day_headers_and_bounded_rows(int dayCount, int columns)
    {
        var clips = Enumerable.Range(0, 1000)
            .Select(i => Clip(i, First.AddDays(-i % dayCount), $"Game {i % 4}")).ToArray();
        var feed = GalleryDayFeed.Build(clips, columns, TimeZoneInfo.Utc);
        var headers = feed.OfType<GalleryDayHeader>().ToArray();
        var rows = feed.OfType<GalleryClipRow>().ToArray();
        Assert.Equal(dayCount, headers.Length);
        Assert.All(rows, row => Assert.InRange(row.Clips.Count, 1, columns));
        Assert.Equal(1000, rows.Sum(row => row.Clips.Count));
        Assert.Equal(1000, rows.SelectMany(row => row.Clips).Select(clip => clip.Path).Distinct().Count());
    }

    private ClipItem Clip(int id, DateTimeOffset captured, string game)
    {
        string path = Path.Combine(_directory, $"{id}.mp4");
        File.WriteAllText(path, "fixture");
        return new ClipItem(new FileInfo(path), new(captured, game), new(3, captured));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
