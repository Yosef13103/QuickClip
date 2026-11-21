using QuickClip.Library;

namespace QuickClip.Gallery;

// Catalog identity is authoritative when known; legacy display-name groups
// are deliberately separate until the owner explicitly associates them.
internal sealed record GalleryFolderKey(string? GameId, string? LegacyName, bool IsAll = false)
{
    internal static GalleryFolderKey All { get; } = new(null, null, true);
    internal static GalleryFolderKey For(ClipDetails details) => details.GameId is { Length: > 0 } id
        ? new(id, null) : new(null, LibraryFiles.TextKey(details.Game));
    internal bool Matches(ClipDetails details) => IsAll || this == For(details);
}

internal sealed record GalleryFolderSelection(GalleryFolderKey Game, bool IsChild = false, Guid? CollectionId = null,
    string? PhysicalDirectory = null)
{
    internal bool Matches(ClipDetails details) => Game.Matches(details) &&
        (!IsChild || details.Collection?.Id == CollectionId);

    // Full-path child selection deliberately remains independent of virtual collection IDs.
    internal bool Matches(ClipDetails details, string mediaPath, GalleryFolderKey presentedGame) =>
        Game.IsAll || (Game == presentedGame &&
            (PhysicalDirectory is { } directory
                ? LibraryFiles.Within(mediaPath, directory)
                : !IsChild || details.Collection?.Id == CollectionId));
}
