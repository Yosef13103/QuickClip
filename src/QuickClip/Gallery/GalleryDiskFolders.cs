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

    // The physical root is the Gallery's presentation parent, regardless of game ID.
    // A saved tModLoader clip in Terraria/Modded/Calamity appears beneath Terraria.
    // The authoritative game ID/name stays unchanged in its ClipDetails metadata.
    internal static string PresentedGame(ClipDetails details, GalleryDiskFolder? folder) =>
        folder?.Game ?? details.Game;

    internal static GalleryFolderKey PresentedKey(ClipDetails details, string label,
        string? uniqueAssociatedId, bool physicalFolder = false)
    {
        // A directory can contain vanilla Terraria and tModLoader (different IDs).
        // Group by the physical parent for the Gallery only; never rewrite metadata.
        if (physicalFolder) return new GalleryFolderKey(null, LibraryFiles.TextKey(label));
        if (details.GameId != null) return GalleryFolderKey.For(details);
        if (uniqueAssociatedId != null) return new GalleryFolderKey(uniqueAssociatedId, null);
        return new GalleryFolderKey(null, LibraryFiles.TextKey(label));
    }
}
