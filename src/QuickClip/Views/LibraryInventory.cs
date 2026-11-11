namespace QuickClip.Views;

internal enum InventoryCategory { Finished, Originals, Exports, ProjectLinked }
internal enum InventoryRootState { Available, Missing, Unavailable }

internal sealed record InventoryRoot(string Path, InventoryRootState State, long? FreeBytes);
internal sealed record InventoryGroup(InventoryCategory Category, string Game, int Clips, long? LogicalBytes);
internal sealed record LibraryInventorySnapshot(IReadOnlyList<InventoryRoot> Roots, IReadOnlyList<InventoryGroup> Groups,
    IReadOnlySet<InventoryCategory> MeasuredCategories, int MissingClips, int UnavailableClips)
{
    internal long? UniqueLogicalBytes { get; init; }
    internal long UnknownIdentityBytes { get; init; }
}

/// <summary>Read the existing index. Implementations should leave scan scheduling and root registration to the library host.</summary>
internal interface ILibraryInventorySource
{
    Task<LibraryInventorySnapshot> ReadAsync(CancellationToken token);
}

internal sealed class StorageDashboardModel(LibraryInventorySnapshot snapshot)
{
    internal sealed record Row(string Label, string Clips, string Size);

    private IEnumerable<InventoryGroup> IndexedMedia => snapshot.Groups.Where(group => group.Category != InventoryCategory.ProjectLinked);

    internal string ClipCount => IndexedMedia.Sum(group => (long)group.Clips).ToString("N0");
    internal string TotalSize => snapshot.UniqueLogicalBytes is long unique
        ? snapshot.UnknownIdentityBytes == 0 ? Fmt.Size(unique) : $"{Fmt.Size(unique)} verified + {Fmt.Size(snapshot.UnknownIdentityBytes)} unverified"
        : SizeOf(IndexedMedia);
    internal string Availability => $"{snapshot.MissingClips:N0} missing · {snapshot.UnavailableClips:N0} unavailable";

    internal IEnumerable<Row> Categories => Enum.GetValues<InventoryCategory>().Select(category =>
    {
        string label = category switch
        {
            InventoryCategory.Finished => "Finished clips",
            InventoryCategory.Originals => "Originals",
            InventoryCategory.Exports => "Exports",
            _ => "Project-linked files",
        };
        return snapshot.MeasuredCategories.Contains(category)
            ? Summarize(label, snapshot.Groups.Where(group => group.Category == category))
            : new Row(label, "—", "Not reported");
    });

    internal IEnumerable<Row> Games => IndexedMedia.GroupBy(group => group.Game, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(game => game.Sum(group => group.LogicalBytes.GetValueOrDefault()))
        .ThenBy(game => game.Key, StringComparer.OrdinalIgnoreCase)
        .Select(game => Summarize(string.IsNullOrWhiteSpace(game.Key) ? "Unclassified" : game.Key, game));

    private static Row Summarize(string label, IEnumerable<InventoryGroup> groups)
    {
        var items = groups.ToArray();
        return new(label, items.Sum(item => (long)item.Clips).ToString("N0"), SizeOf(items));
    }

    private static string SizeOf(IEnumerable<InventoryGroup> groups)
    {
        var bytes = groups.Select(group => group.LogicalBytes).ToArray();
        long known = bytes.Sum(size => size.GetValueOrDefault());
        return bytes.Any(size => size == null)
            ? bytes.All(size => size == null) ? "Unknown" : $"{Fmt.Size(known)} known"
            : Fmt.Size(known);
    }
}
