using QuickClip.Library;

namespace QuickClip.Gallery;

/// <summary>Read-only physical folder provenance. This never changes the game's catalog identity,
/// persisted collection, source permission, or clip metadata.</summary>
internal sealed record GalleryDiskFolder(string Root, string Game, string[] Children)
{
    internal IEnumerable<(string Path, string? Parent, string Name, int Depth)> ChildAncestors()
    {
        for (int i = 0; i < Children.Length; i++)
        {
            string path = Path.Combine([Root, Game, .. Children.Take(i + 1)]);
            string? parent = i == 0 ? null : Path.Combine([Root, Game, .. Children.Take(i)]);
            yield return (path, parent, Children[i], i + 1);
        }
    }
}

internal static class GalleryDiskFolders
{
    internal static GalleryDiskFolder? Locate(string mediaPath, IEnumerable<string> approvedRoots)
    {
        string? root = approvedRoots.Where(folder => LibraryFiles.Within(mediaPath, folder))
            .OrderByDescending(folder => folder.Length).FirstOrDefault();
        if (root == null || Path.GetDirectoryName(mediaPath) is not { } parent) return null;
        string relative = Path.GetRelativePath(root, parent);
        if (relative is "." or "" || Path.IsPathRooted(relative) ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part is "." or "..")) return null;
        string[] segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? null : new(root, segments[0], segments.Skip(1).ToArray());
    }

    // Legacy Personal scanner historically used the last folder's name as the game.
    // Only infer the physical parent when the persisted label still matches that leaf.
    internal static string PresentedGame(ClipDetails details, GalleryDiskFolder? folder) =>
        details.GameId != null || folder is not { Children.Length: > 0 } ||
        !string.Equals(details.Game, folder.Children[^1], StringComparison.OrdinalIgnoreCase)
            ? details.Game : folder.Game;

    internal static GalleryFolderKey PresentedKey(ClipDetails details, string label, string? uniqueAssociatedId)
    {
        // View-only alias: a distinct established ID may supply the presentation group,
        // never the stored game ID or the authorization to capture.
        if (details.GameId != null) return GalleryFolderKey.For(details);
        if (uniqueAssociatedId != null) return new GalleryFolderKey(uniqueAssociatedId, null);
        return new GalleryFolderKey(null, LibraryFiles.TextKey(label));
    }
}
