using System.Collections.Immutable;

namespace QuickClip.Library;

internal enum MediaCategory { ImportedFinished, Legacy, QuickClipOriginal, Export }
internal enum ClipAvailability { Available, Missing, Unavailable }
internal enum CaptureDateSource { FileTime, Embedded, LegacyDetails }
internal enum LegacyMetadataStatus { None, Imported, StatMismatch, Unreadable }
internal enum BookmarkOrigin { Editor, Live }
internal enum ClipSort { CapturedAt, Game, Title, Duration, Category }

internal sealed record LibraryRoot(Guid Id, string Directory, MediaCategory Category,
    ImmutableArray<string> ExcludedDirectories, ClipAvailability Availability, DateTimeOffset? LastScanAt);

internal sealed record LibraryScanOptions(int MaxEntries = 100_000, int MaxDepth = 16,
    string? RelativeDirectory = null);

internal sealed record LibraryScanResult(
    Guid RootId, int Visited, int Indexed, int HeaderReads, int MarkedMissing,
    int Unreadable, bool Complete, ClipAvailability RootAvailability);

internal sealed record IndexedClip(Guid Id, Guid RootId, string Path, MediaCategory Category,
    DateTimeOffset CapturedAt, CaptureDateSource DateSource, string Game, string Title, string? GameId,
    TimeSpan? Duration, long Bytes, ClipAvailability Availability, LegacyMetadataStatus LegacyMetadata,
    bool Favorite, long Revision, Guid? OriginalId, Guid? SessionId);

internal sealed record ClipQuery
{
    public string? Search { get; init; }
    public string? Game { get; init; }
    public string? GameId { get; init; }
    public string? Tag { get; init; }
    public Guid? RootId { get; init; }
    public MediaCategory? Category { get; init; }
    public ClipAvailability? Availability { get; init; }
    public bool? Favorite { get; init; }
    public DateTimeOffset? CapturedFrom { get; init; }
    public DateTimeOffset? CapturedBefore { get; init; }
    public TimeSpan? MinDuration { get; init; }
    public TimeSpan? MaxDuration { get; init; }
    public ClipSort Sort { get; init; }
    public bool Descending { get; init; } = true;
    public int Offset { get; init; }
    public int Limit { get; init; } = 50;
}

internal sealed record ClipBookmark(Guid Id, TimeSpan Offset, string? Label, BookmarkOrigin Origin,
    Guid? SessionId = null, DateTimeOffset? CapturedAt = null);

internal sealed record ClipAnnotations(long Revision, bool Favorite, ImmutableArray<string> Tags,
    ImmutableArray<ClipBookmark> Bookmarks);

internal sealed record StorageGroup(Guid RootId, MediaCategory Category, string Game, int Clips, long LogicalBytes);
internal sealed record RootSpace(Guid RootId, long? FreeBytes);
internal sealed record StorageInventory(ImmutableArray<StorageGroup> AvailableGroups, long KnownUniqueLogicalBytes,
    long UnidentifiedLogicalBytes, int MissingClips, int UnavailableClips, int OriginalsWithoutExport,
    ImmutableArray<RootSpace> RootSpace)
{
    public static long EstimateBytes(int videoKbps, int audioKbps, TimeSpan duration)
    {
        if (videoKbps < 0 || audioKbps < 0 || duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        return checked((long)decimal.Ceiling(((decimal)videoKbps + audioKbps) * 125 * duration.Ticks / TimeSpan.TicksPerSecond));
    }
}

internal sealed class LibraryConflictException(string message) : InvalidOperationException(message);
