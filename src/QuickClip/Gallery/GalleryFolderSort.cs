namespace QuickClip.Gallery;

internal static class GalleryFolderSort
{
    internal static IEnumerable<T> Apply<T>(IEnumerable<T> source, Func<T, string> name, Func<T, DateTime> date, bool byName, bool descending)
        => byName
            ? (descending ? source.OrderByDescending(name, StringComparer.OrdinalIgnoreCase) : source.OrderBy(name, StringComparer.OrdinalIgnoreCase))
            : (descending ? source.OrderByDescending(date) : source.OrderBy(date)).ThenBy(name, StringComparer.OrdinalIgnoreCase);
}
