using QuickClip.Gallery;

namespace QuickClip.Library;

internal static class SavedRecordingFolders
{
    internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal static string[] Additional(AppSettings settings) => Additional(settings.ClipsFolder, settings.AdditionalClipSources);

    internal static string[] Additional(string destination, IEnumerable<string>? sources)
    {
        var result = new List<string>();
        string? primary = Normalize(destination);
        foreach (string raw in sources ?? [])
        {
            if (Normalize(raw) is not { } path ||
                primary != null && Overlaps(primary, path) || result.Any(other => Overlaps(other, path))) continue;
            result.Add(path);
        }
        return result.ToArray();
    }

    internal static (string Destination, string[] Sources) PlanChange(AppSettings settings, string destination)
    {
        string next = RequireAvailable(destination);
        if (Paths.Equals(next, Normalize(settings.ClipsFolder)))
            return (next, Additional(settings));
        if (Normalize(settings.ClipsFolder) is not { } previous)
            throw new ArgumentException("The current destination cannot be retained as a safe Gallery source.");
        var sources = Additional(settings).Where(path => !Paths.Equals(path, next)).Append(previous).ToArray();
        if (sources.Any(path => Overlaps(path, next)))
            throw new ArgumentException("Choose a destination outside the existing Gallery sources. Nested folders can count the same recordings twice.");
        return (next, sources);
    }

    internal static string[] PlanAdd(AppSettings settings, string directory)
    {
        string path = RequireAvailable(directory);
        var sources = Additional(settings);
        if (sources.Contains(path, Paths) || Paths.Equals(path, Normalize(settings.ClipsFolder))) return sources;
        if (Normalize(settings.ClipsFolder) is { } primary && Overlaps(path, primary) ||
            sources.Any(root => Overlaps(path, root)))
            throw new ArgumentException("Choose a folder outside the current destination and existing Gallery sources.");
        return [.. sources, path];
    }

    private static string RequireAvailable(string directory)
    {
        string path = LibraryFiles.RootPath(directory);
        if (LibraryFiles.InspectPath(path, directory: true) != ClipAvailability.Available)
            throw new ArgumentException("Choose an available local folder. Linked and offline folders cannot be added.");
        return path;
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return LibraryFiles.RootPath(path); }
        catch (ArgumentException) { return null; }
    }

    private static bool Overlaps(string a, string b) => LibraryFiles.Within(a, b) || LibraryFiles.Within(b, a);

    internal static bool ContainsMedia(AppSettings settings, string path) =>
        !ClipLibrary.IsMomentsPath(path) && Additional(settings).Prepend(settings.ClipsFolder)
            .Any(root => LibraryFiles.Within(path, root) &&
                !LibraryFiles.IsExcluded(root, path, [.. settings.LibraryExclusions ?? []]));
}
