using QuickClip.Library;
using QuickClip.Views;

namespace QuickClip.Gallery;

internal readonly record struct ClipMediaStamp(long Length, DateTime Modified, DateTime Created)
{
    public static ClipMediaStamp Read(FileInfo file) => new(file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
}

internal sealed record ClipAnnotationTarget(string Path, ClipMediaStamp Stamp, ClipDetails Details)
{
    internal FileStamp? Source { get; init; }

    public static ClipAnnotationTarget For(ClipItem clip) => new(clip.Path, ClipMediaStamp.Read(clip.File), clip.Details)
    {
        Source = ReadSource(clip.Path),
    };

    internal static FileStamp? ReadSource(string path) =>
        EditorSource.Check(path, null, out var stamp) == EditorSourceState.Ready ? stamp : null;
}

internal sealed record ClipAnnotationResult(string Path, ClipDetails? Details, string? Error);

/// <summary>Edits an existing profile record. The library adapter will use clip IDs and revisions in place of path and file facts.</summary>
internal interface IClipAnnotationActions
{
    Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token);
    Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
        string? addTag, bool? favorite, CancellationToken token);
}

internal sealed class LegacyClipAnnotationActions : IClipAnnotationActions
{
    public static LegacyClipAnnotationActions Instance { get; } = new();
    private readonly object _gate = new();

    public Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.Run(() => SaveDraft(target, draft, token), token);
    }

    public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
        string? addTag, bool? favorite, CancellationToken token) => Task.Run<IReadOnlyList<ClipAnnotationResult>>(() =>
    {
        var results = new List<ClipAnnotationResult>(targets.Count);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            results.Add(Update(target, current =>
            {
                string[] tags = current.Tags;
                if (addTag != null)
                {
                    string tag = addTag.Trim();
                    if (!ClipDetails.IsValidTag(tag)) throw new AnnotationEditException("Enter a valid tag.");
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        if (tags.Length >= ClipDetails.MaxTags) throw new AnnotationEditException("This clip is at the tag limit.");
                        tags = [.. tags, tag];
                    }
                }
                return current with { Tags = tags, IsFavorite = favorite ?? current.IsFavorite };
            }, token));
        }
        return results;
    }, token);

    internal ClipAnnotationResult SaveDraft(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token = default) => Update(target, current =>
    {
        if (!SameDetails(current, target.Details))
            throw new AnnotationEditException("Clip details changed. Cancel this draft and reopen the clip to see the saved details.");
        return draft;
    }, token);

    private ClipAnnotationResult Update(ClipAnnotationTarget target, Func<ClipDetails, ClipDetails> edit, CancellationToken token)
    {
        lock (_gate)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!AppProfile.IsPersonal || ClipLibrary.IsMomentsPath(target.Path))
                    throw new AnnotationEditException("Annotations are unavailable for this clip.");
                using var lease = LibraryFiles.OpenMedia(target.Path);
                var live = LibraryFiles.ReadStamp(lease);
                if (live.Bytes != target.Stamp.Length || live.Created != target.Stamp.Created.Ticks ||
                    live.Modified != target.Stamp.Modified.Ticks || (target.Source is { } expected && live != expected))
                    throw new AnnotationEditException("The media file changed or is unavailable.");
                var file = new FileInfo(target.Path);
                var current = ClipDetailsStore.Read(file, target.Details.Game, target.Details.CapturedAt);
                var updated = edit(current).Normalize();
                if (!SameDetails(current, updated)) ClipDetailsStore.Write(target.Path, updated, token);
                return new(target.Path, updated, null);
            }
            catch (AnnotationEditException ex) { return new(target.Path, null, ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Failure(DiagnosticCategory.Library, "Could not save clip annotations", ex);
                return new(target.Path, null, "Couldn’t save clip annotations.");
            }
        }
    }

    private static bool SameDetails(ClipDetails left, ClipDetails right) =>
        left.CapturedAt == right.CapturedAt && left.Game == right.Game && left.Title == right.Title &&
        left.IsFavorite == right.IsFavorite && left.Tags.SequenceEqual(right.Tags) && left.Bookmarks.SequenceEqual(right.Bookmarks);

    private sealed class AnnotationEditException(string message) : Exception(message);
}

internal static class ClipTagChoices
{
    internal static IEnumerable<string> Match(IEnumerable<string> tags, string text, IEnumerable<string> exclude)
    {
        string query = text.Trim();
        return tags.Except(exclude, StringComparer.OrdinalIgnoreCase)
            .Where(tag => tag.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(tag => tag.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(tag => tag, StringComparer.OrdinalIgnoreCase);
    }
}
