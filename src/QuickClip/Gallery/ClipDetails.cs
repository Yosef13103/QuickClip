using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickClip.Gallery;

internal sealed record ClipBookmark(double Seconds, string Label)
{
    internal const int MaxLabelLength = 64;

    internal string TimeText => Fmt.Time(Seconds);
}

internal sealed record ClipDetails(DateTimeOffset CapturedAt, string Game, string? Title = null)
{
    internal const int MaxTags = 16;
    internal const int MaxTagLength = 32;
    internal const int MaxBookmarks = 32;

    public string[] Tags { get; init; } = [];
    public bool IsFavorite { get; init; }
    public ClipBookmark[] Bookmarks { get; init; } = [];

    internal static bool IsValidTag(string tag) =>
        tag.Length is > 0 and <= MaxTagLength && !tag.Any(char.IsControl);

    internal bool MatchesOrganizationFilter(string tagSearch, bool favoritesOnly) =>
        (!favoritesOnly || IsFavorite) &&
        (tagSearch.Length == 0 || Tags.Any(tag => tag.Contains(tagSearch, StringComparison.OrdinalIgnoreCase)));

    internal ClipDetails Normalize() => this with
    {
        Tags = (Tags ?? []).OfType<string>().Select(tag => tag.Trim()).Where(IsValidTag)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxTags).ToArray(),
        Bookmarks = (Bookmarks ?? []).OfType<ClipBookmark>()
            .Where(mark => double.IsFinite(mark.Seconds) && mark.Seconds >= 0 &&
                mark.Seconds <= TimeSpan.MaxValue.TotalSeconds - 1 &&
                mark.Label?.Trim() is { Length: > 0 and <= ClipBookmark.MaxLabelLength } label &&
                !label.Any(char.IsControl))
            .Select(mark => mark with { Label = mark.Label.Trim() })
            .DistinctBy(mark => mark.Seconds).Take(MaxBookmarks).OrderBy(mark => mark.Seconds).ToArray(),
    };

    internal ClipDetails ForExport(double start, double end) => this with
    {
        Bookmarks = Bookmarks.Where(mark => mark.Seconds >= start && mark.Seconds < end)
            .Select(mark => mark with { Seconds = mark.Seconds - start }).ToArray(),
    };
}

/// <summary>Profile-local metadata for this milestone. The media and its filename remain independent.</summary>
internal static class ClipDetailsStore
{
    private sealed record Entry(long CreatedTicks, long ModifiedTicks, long Length, ClipDetails Details);

    private static string MetadataPath(FileInfo file) => Path.Combine(AppProfile.LocalDirectory, "clip-details",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.FullName.ToUpperInvariant()))) + ".json");

    public static ClipDetails Read(FileInfo file, string game, DateTimeOffset? embedded)
    {
        if (!AppProfile.IsPersonal) return new ClipDetails(new DateTimeOffset(file.CreationTime), game);
        string path = MetadataPath(file);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)) is { } entry &&
                entry.CreatedTicks == file.CreationTimeUtc.Ticks && entry.ModifiedTicks == file.LastWriteTimeUtc.Ticks &&
                entry.Length == file.Length && entry.Details is { Game.Length: > 0 } details && details.CapturedAt != default)
                return details.Normalize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("Could not read saved clip details: " + ex.Message);
        }
        DateTime fallback = file.CreationTimeUtc <= file.LastWriteTimeUtc ? file.CreationTimeUtc : file.LastWriteTimeUtc;
        return new ClipDetails((embedded ?? new DateTimeOffset(fallback)).ToLocalTime(), game);
    }

    public static void Write(string mediaPath, ClipDetails details)
    {
        var file = new FileInfo(mediaPath);
        string path = MetadataPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staged, JsonSerializer.Serialize(new Entry(file.CreationTimeUtc.Ticks, file.LastWriteTimeUtc.Ticks, file.Length, details.Normalize())));
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    public static void TryWrite(string path, ClipDetails details)
    {
        try { Write(path, details); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not save clip details: " + ex.Message);
        }
    }
}
