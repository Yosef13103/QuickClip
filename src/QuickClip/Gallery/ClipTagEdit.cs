using QuickClip.Library;

namespace QuickClip.Gallery;

internal sealed record ClipTagCount(string Name, int Count)
{
    public string Label => $"{Name} ({Count:N0})";
}

/// <summary>Edits tags on explicitly selected clips only. Never renames media or
/// changes other annotations. Existing annotation actions own persistence and conflicts.</summary>
internal sealed class ClipTagEdit
{
    internal string Source { get; }
    internal string? Replacement { get; }
    private readonly string _key;

    internal ClipTagEdit(string source, string? replacement)
    {
        Source = Validate(source);
        _key = LibraryFiles.TextKey(Source);
        Replacement = string.IsNullOrWhiteSpace(replacement) ? null : Validate(replacement);
    }

    private static string Validate(string value)
    {
        string name = value.Trim().Normalize();
        if (!ClipDetails.IsValidTag(name)) throw new ArgumentException("Enter a valid tag.");
        return name;
    }

    internal bool Matches(ClipDetails details) =>
        details.Tags.Any(tag => LibraryFiles.TextKey(tag) == _key);

    internal ClipDetails Apply(ClipDetails details)
    {
        if (!Matches(details)) return details;
        // An existing canonical replacement keeps its position. Case-only changes
        // are not a global tag rename; the index owns the shared display spelling.
        if (Replacement != null && LibraryFiles.TextKey(Replacement) == _key) return details;
        var remaining = details.Tags.Where(tag => LibraryFiles.TextKey(tag) != _key).ToList();
        if (Replacement != null && !remaining.Any(tag => LibraryFiles.TextKey(tag) == LibraryFiles.TextKey(Replacement)))
            remaining.Add(Replacement);
        return details with { Tags = remaining.ToArray() };
    }

    internal static ClipTagCount[] Counts(IEnumerable<ClipDetails> clips) =>
        clips.SelectMany(details => details.Tags.DistinctBy(LibraryFiles.TextKey))
            .GroupBy(LibraryFiles.TextKey)
            .Select(group => new ClipTagCount(group.OrderBy(tag => tag, StringComparer.Ordinal).First(), group.Count()))
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal async Task<IReadOnlyList<ClipAnnotationResult>> SaveAsync(IClipAnnotationActions actions,
        IReadOnlyList<ClipAnnotationTarget> targets, CancellationToken token)
    {
        var results = new List<ClipAnnotationResult>(targets.Count);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            var draft = Apply(target.Details);
            if (ReferenceEquals(draft, target.Details)) continue;
            results.Add(await actions.SaveDraftAsync(target, draft, token));
        }
        return results;
    }
}
