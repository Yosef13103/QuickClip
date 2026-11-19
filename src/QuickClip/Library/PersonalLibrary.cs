using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Library;

/// <summary>Connection between the configured clips folders and the profile-local index.</summary>
internal sealed class PersonalLibrary : IClipAnnotationActions, ILibraryInventorySource
{
    public static PersonalLibrary Current { get; } = new();
    public static bool Enabled => AppProfile.IsPersonal && AppSettings.Current.EnableLibraryIndex;
    private readonly SemaphoreSlim _scanGate = new(1);
    private readonly Lazy<LibraryIndex> _index = new(LibraryIndex.ForCurrentProfile);

    internal async Task<(List<ClipItem> Clips, LibraryScanResult Scan)> LoadAsync(
        string root, string[] exclusions, string[]? scopes, CancellationToken token,
        Action<string, double?>? progress = null)
    {
        var index = _index.Value;
        progress?.Invoke("Waiting for library index", null);
        await _scanGate.WaitAsync(token);
        Guid id;
        LibraryScanResult scan;
        try
        {
            id = await index.RegisterRootAsync(root, exclusions: exclusions, cancellationToken: token, updateExclusions: true);
            progress?.Invoke("Indexing Gallery", null);
            Action<int, int>? report = progress == null ? null : (visited, indexed) =>
                progress($"Indexing · {indexed:N0} clips found, {visited:N0} entries checked", null);
            scan = await index.ReconcileAsync(id, new(RelativeDirectory: scopes is { Length: > 0 } ? scopes[0] : null), token, report);
            if (scopes != null)
                foreach (string scope in scopes.Skip(1))
                {
                    var next = await index.ReconcileAsync(id, new(RelativeDirectory: scope), token, report);
                    if (!next.Complete) scan = next;
                }
        }
        finally { _scanGate.Release(); }

        progress?.Invoke("Reading indexed clips", null);
        var clips = await ReadIndexedClipsAsync(token, id, progress);
        return (clips, scan);
    }

    internal async Task<List<ClipItem>> ReadIndexedClipsAsync(CancellationToken token, Guid? id = null,
        Action<string, double?>? progress = null, IReadOnlyList<string>? folders = null)
    {
        var rows = await _index.Value.ReadGalleryAsync(id, null, token);
        return await Task.Run(() =>
        {
            var items = new List<ClipItem>(rows.Count);
            int checkedRows = 0;
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (progress != null && (++checkedRows % 128 == 0 || checkedRows == rows.Count))
                    progress($"Loading clips · {checkedRows:N0}/{rows.Count:N0}", (double)checkedRows / rows.Count);
                if (ClipLibrary.IsMomentsPath(row.Path) ||
                    (folders != null && !folders.Any(folder => LibraryFiles.Within(row.Path, folder)))) continue;
                var file = new FileInfo(row.Path);
                try
                {
                    if (file.Exists && ClipMediaStamp.Read(file) == row.Stamp)
                        items.Add(new(file, row.Details, new(row.Duration, row.Details.CapturedAt)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return items;
        }, token);
    }

    internal async Task<ClipDetails?> ReadAsync(string path, CancellationToken token = default)
    {
        if (!Enabled || !WithinConfiguredRoot(path)) return null;
        try
        {
            var row = (await _index.Value.ReadGalleryAsync(null, path, token)).SingleOrDefault();
            return await Task.Run(() =>
            {
                if (row == null) return null;
                using var file = LibraryFiles.OpenMedia(path);
                var stamp = LibraryFiles.ReadStamp(file);
                return stamp == new FileStamp(row.Stamp.Created.Ticks, row.Stamp.Modified.Ticks, row.Stamp.Length, row.Identity)
                    ? row.Details : null;
            }, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not read indexed clip details", ex);
            return null;
        }
    }

    internal Task<StoredClipDraft?> ReadEditDraftAsync(string path, FileStamp stamp) =>
        _index.Value.ReadEditDraftAsync(path, stamp);

    internal Task SaveEditDraftAsync(string path, FileStamp stamp, string? previous, ClipEditDraft? draft, CancellationToken token = default) =>
        _index.Value.SaveEditDraftAsync(path, stamp, previous, draft, token);

    internal Task<Dictionary<string, ClipDraftRange>> ReadDraftRangesAsync(IReadOnlyList<ClipItem> clips, CancellationToken token) =>
        _index.Value.ReadDraftRangesAsync(clips, token);

    private static bool WithinConfiguredRoot(string path) => SavedRecordingFolders.ContainsMedia(AppSettings.Current, path);

    public async Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
    {
        if (!Enabled || !WithinConfiguredRoot(target.Path))
            return await LegacyClipAnnotationActions.Instance.SaveDraftAsync(target, draft, token);
        try
        {
            if (target.Details.IndexId == null)
            {
                // A freshly recorded clip can open while Gallery is still indexing it.
                var indexed = await ReadAsync(target.Path, token);
                if (indexed == null)
                    return new(target.Path, null, "The clip is waiting to be indexed. Retry when Gallery has loaded it.");
                var original = target.Details;
                if (indexed.Title != original.Title || indexed.Game != original.Game ||
                    indexed.IsFavorite != original.IsFavorite || !indexed.Tags.SequenceEqual(original.Tags) ||
                    !indexed.Bookmarks.Select(mark => (mark.Seconds, mark.Label))
                        .SequenceEqual(original.Bookmarks.Select(mark => (mark.Seconds, mark.Label))))
                    return new(target.Path, null, "Clip details changed while it was being indexed. Revert and try again.");
                target = target with { Details = indexed };
                draft = draft with { Bookmarks = indexed.Bookmarks };
            }
            var revision = await _index.Value.SaveGalleryDetailsAsync(target, draft, token);
            var row = (await _index.Value.ReadGalleryAsync(null, target.Path)).SingleOrDefault();
            if (row == null || row.Details.IndexId != target.Details.IndexId)
                return new(target.Path, null, "Saved, but the media is no longer available.");
            if (row.Details.IndexRevision != revision)
                return new(target.Path, null, "Saved, but clip details changed again. Revert to load the latest revision.");
            return new(target.Path, row.Details, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not save indexed clip details", ex);
            return new(target.Path, null, ex is LibraryConflictException or ArgumentException ? ex.Message :
                "Couldn’t save clip details to the index.");
        }
    }

    public async Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
        string? addTag, bool? favorite, CancellationToken token)
    {
        var results = new List<ClipAnnotationResult>(targets.Count);
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (!Enabled || !WithinConfiguredRoot(target.Path))
            {
                results.AddRange(await LegacyClipAnnotationActions.Instance.ApplyAsync([target], addTag, favorite, token));
                continue;
            }
            var details = target.Details;
            if (addTag != null && !details.Tags.Contains(addTag, StringComparer.OrdinalIgnoreCase))
            {
                if (!ClipDetails.IsValidTag(addTag) || details.Tags.Length >= ClipDetails.MaxTags)
                {
                    results.Add(new(target.Path, null, "Invalid tag or the clip is at the tag limit."));
                    continue;
                }
                details = details with { Tags = [.. details.Tags, addTag] };
            }
            results.Add(await SaveDraftAsync(target, details with { IsFavorite = favorite ?? details.IsFavorite }, token));
        }
        return results;
    }

    // Explicit available-index snapshot across configured folders, independent
    // of Gallery search/folder filters. Unavailable and excluded media are not edited.
    internal async Task<ClipAnnotationTarget[]> ReadTagTargetsAsync(string tag, CancellationToken token)
    {
        if (!Enabled || !ClipDetails.IsValidTag(tag))
            throw new ArgumentException("Choose a valid indexed tag.");
        var rows = await _index.Value.ReadGalleryAsync(null, null, token, exactTag: tag);
        var targets = rows.Where(row => WithinConfiguredRoot(row.Path)).Take(257).ToArray();
        if (targets.Length > 256)
            throw new LibraryConflictException("More than 256 indexed clips use this tag. Use smaller selections.");
        return targets.Select(row => new ClipAnnotationTarget(row.Path, row.Stamp, row.Details)
        {
            Source = new FileStamp(row.Stamp.Created.Ticks, row.Stamp.Modified.Ticks, row.Stamp.Length, row.Identity),
        }).ToArray();
    }

    internal Task<int> SetTagColorAsync(string tag, string? color, CancellationToken token)
    {
        if (!Enabled) throw new LibraryConflictException("Tag colors require the library index.");
        return _index.Value.SetTagColorAsync(tag, color, token);
    }

    internal Task<int> ApplyTagDeltaAsync(IReadOnlyCollection<ClipAnnotationTarget> targets,
        IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken token,
        bool replaceOnlyPresent = false, bool? hidden = null)
    {
        if (!Enabled || targets.Any(target => !WithinConfiguredRoot(target.Path)))
            throw new LibraryConflictException("Tag management requires clips in configured indexed folders.");
        return _index.Value.ApplyTagDeltaAsync(targets, add, remove, token, replaceOnlyPresent, hidden);
    }

    // This is a catalog estimate, not allocated disk usage. Only configured roots
    // on the destination volume count; do not scan the drive to answer a UI query.
    internal async Task<long?> DestinationTrackedBytesAsync(string destination, string[] sources)
    {
        if (!Enabled) return null;
        var index = _index.Value;
        var roots = await index.GetRootsAsync();
        var inventory = await index.GetStorageInventoryAsync();
        return await Task.Run<long?>(() =>
        {
            string? volume = LibraryFiles.RootVolume(destination);
            if (volume == null) return null;
            var configured = sources.Prepend(destination).ToHashSet(SavedRecordingFolders.Paths);
            var measured = roots.Where(root => configured.Contains(root.Directory) &&
                root.Availability == ClipAvailability.Available && root.LastScanAt != null &&
                string.Equals(LibraryFiles.RootVolume(root.Directory), volume, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!measured.Any(root => SavedRecordingFolders.Paths.Equals(root.Directory, destination))) return null;
            var ids = measured.Select(root => root.Id).ToHashSet();
            return inventory.AvailableGroups.Where(group => ids.Contains(group.RootId)).Sum(group => group.LogicalBytes);
        });
    }

    public async Task<LibraryInventorySnapshot> ReadAsync(CancellationToken token)
    {
        if (!Enabled) throw new InvalidOperationException("Enable indexing in Settings to see storage inventory.");
        var roots = await _index.Value.GetRootsAsync(token);
        var inventory = await _index.Value.GetStorageInventoryAsync(token);
        var space = inventory.RootSpace.ToDictionary(r => r.RootId);
        return new(roots.Select(r => new InventoryRoot(r.Directory, (InventoryRootState)r.Availability,
                space.GetValueOrDefault(r.Id)?.FreeBytes)).ToArray(),
            inventory.AvailableGroups.Select(g => new InventoryGroup(g.Category switch
            {
                MediaCategory.QuickClipOriginal => InventoryCategory.Originals,
                MediaCategory.Export => InventoryCategory.Exports,
                _ => InventoryCategory.Finished,
            }, g.Game, g.Clips, g.LogicalBytes)).ToArray(),
            new HashSet<InventoryCategory> { InventoryCategory.Finished, InventoryCategory.Originals, InventoryCategory.Exports },
            inventory.MissingClips, inventory.UnavailableClips)
        { UniqueLogicalBytes = inventory.KnownUniqueLogicalBytes, UnknownIdentityBytes = inventory.UnidentifiedLogicalBytes };
    }
}
