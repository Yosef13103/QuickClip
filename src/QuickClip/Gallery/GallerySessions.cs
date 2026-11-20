namespace QuickClip.Gallery;

internal sealed record GallerySessionKey(GalleryFolderKey Game, Guid? RecordedId, string? LegacyAnchor);

public sealed class GallerySessionCard
{
    internal GallerySessionCard(GallerySessionKey key, IReadOnlyList<ClipItem> clips, TimeZoneInfo zone)
    {
        Key = key;
        Clips = clips;
        Representative = clips[0];
        var first = clips.Min(c => c.Details.CapturedAt);
        var last = clips.Max(c => c.Details.CapturedAt);
        Heading = TimeZoneInfo.ConvertTime(last, zone).ToString("f");
        string range = first == last ? "" : $" · capture starts {TimeZoneInfo.ConvertTime(first, zone):g} – {TimeZoneInfo.ConvertTime(last, zone):g}";
        Summary = $"{clips.Count:N0} {(clips.Count == 1 ? "clip" : "clips")} · {Fmt.Size(clips.Sum(c => c.Size))}{range}";
        var collections = clips.Select(c => c.Details.Collection?.Name ?? "Uncategorized").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Context = $"{Representative.Folder} · {(collections.Length == 1 ? collections[0] : "Mixed collections")} · {(key.RecordedId == null ? "Approximate session" : "Recorded session")}";
    }
    internal GallerySessionKey Key { get; }
    internal IReadOnlyList<ClipItem> Clips { get; }
    public ClipItem Representative { get; }
    public string Heading { get; }
    public string Summary { get; }
    public string Context { get; }
}

internal static class GallerySessions
{
    // Real replay-session identity wins. Historical clips have only an approximate
    // 30-minute capture-start gap heuristic; partition by game before grouping.
    // Build membership before projecting search/tag/collection filters.
    internal static GallerySessionCard[] Build(IEnumerable<ClipItem> library, IEnumerable<ClipItem> matching,
        TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var visible = matching.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cards = new List<GallerySessionCard>();
        foreach (var game in library.GroupBy(c => GalleryFolderKey.For(c.Details)))
        {
            void Add(GallerySessionKey key, IEnumerable<ClipItem> members)
            {
                var clips = members.Where(c => visible.Contains(c.Path))
                    .OrderByDescending(c => c.Details.CapturedAt).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                if (clips.Length > 0) cards.Add(new(key, clips, zone));
            }
            foreach (var recorded in game.Where(c => c.Details.RecordingSessionId is Guid id && id != Guid.Empty)
                .GroupBy(c => c.Details.RecordingSessionId))
                Add(new(game.Key, recorded.Key, null), recorded);
            var legacy = new List<ClipItem>();
            foreach (var clip in game.Where(c => !c.Details.RecordingSessionId.HasValue || c.Details.RecordingSessionId == Guid.Empty)
                .OrderBy(c => c.Details.CapturedAt).ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (legacy.Count > 0 && clip.Details.CapturedAt - legacy[^1].Details.CapturedAt > TimeSpan.FromMinutes(30))
                {
                    Add(new(game.Key, null, legacy[0].Path.ToUpperInvariant()), legacy);
                    legacy.Clear();
                }
                legacy.Add(clip);
            }
            if (legacy.Count > 0) Add(new(game.Key, null, legacy[0].Path.ToUpperInvariant()), legacy);
        }
        return cards.OrderByDescending(c => c.Representative.Details.CapturedAt)
            .ThenBy(c => c.Representative.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
