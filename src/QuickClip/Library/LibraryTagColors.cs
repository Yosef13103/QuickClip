using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed record TagColorChoice(string Label, string? Hex)
{
    public override string ToString() => Label;
}

internal static class TagPalette
{
    internal static readonly TagColorChoice[] Choices =
    [
        new("Default", null), new("Red", "#F87171"), new("Orange", "#FB923C"),
        new("Yellow", "#FACC15"), new("Green", "#4ADE80"), new("Teal", "#2DD4BF"),
        new("Blue", "#60A5FA"), new("Purple", "#A78BFA"), new("Pink", "#F472B6"),
    ];

    internal static bool Valid(string? color) => Choices.Any(choice => choice.Hex == color);
}

internal sealed partial class LibraryIndex
{
    // Presentation only: one normalized tag name has one color across the index.
    // This never modifies clip annotations, paths or source revisions.
    internal Task<int> SetTagColorAsync(string tag, string? color, CancellationToken token = default)
    {
        string name = tag.Trim().Normalize();
        if (!ClipDetails.IsValidTag(name) || !TagPalette.Valid(color))
            throw new ArgumentException("Invalid tag or color.");
        return Run(token, ct =>
        {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            int changed = Execute(connection, transaction,
                "UPDATE tags SET color=$color WHERE game_id='' AND name_key=$key AND color IS NOT $color;",
                ("$color", color), ("$key", LibraryFiles.TextKey(name)));
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return changed;
        });
    }
}
