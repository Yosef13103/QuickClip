using System.ComponentModel;

namespace QuickClip.Gallery;

public sealed class GalleryDayHeader : INotifyPropertyChanged
{
    private bool? _selection;
    internal GalleryDayHeader(DateTime day, IReadOnlyList<ClipItem> clips)
    {
        Day = day;
        Clips = clips;
        var games = clips.Select(clip => clip.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string summary = string.Join(", ", games.Take(2));
        if (games.Length > 2) summary += $" +{games.Length - 2} more";
        Summary = $"{summary} · {clips.Count:N0} {(clips.Count == 1 ? "clip" : "clips")}";
        RefreshSelection();
    }

    internal DateTime Day { get; }
    internal IReadOnlyList<ClipItem> Clips { get; }
    public string Heading => Day.ToString("D");
    public string Summary { get; }
    public string SelectionLabel => $"Select clips for {Heading}";
    public bool? Selection => _selection;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshSelection()
    {
        int selected = Clips.Count(clip => clip.IsSelected);
        bool? state = selected == 0 ? false : selected == Clips.Count ? true : null;
        if (_selection == state) return;
        _selection = state;
        PropertyChanged?.Invoke(this, new(nameof(Selection)));
    }
}

public sealed record GalleryClipRow(IReadOnlyList<ClipItem> Clips);

internal static class GalleryDayFeed
{
    internal static List<object> Build(IReadOnlyList<ClipItem> clips, int columns, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var rows = new List<object>();
        foreach (var group in clips.GroupBy(clip => TimeZoneInfo.ConvertTime(clip.Details.CapturedAt, zone).Date)
            .OrderByDescending(group => group.Key))
        {
            var day = group.ToArray();
            rows.Add(new GalleryDayHeader(group.Key, day));
            rows.AddRange(day.Chunk(columns).Select(row => new GalleryClipRow(row)));
        }
        return rows;
    }

    internal static List<string> SelectedPaths(IEnumerable<ClipItem> visible) =>
        visible.Where(clip => clip.IsSelected).OrderBy(clip => clip.Details.CapturedAt).Select(clip => clip.Path).ToList();
}
