using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Library;

/// <summary>Opt-in connection between the configured clips folder and the profile-local index.</summary>
internal sealed class PersonalLibrary : IClipAnnotationActions, ILibraryInventorySource
{
    public static PersonalLibrary Current { get; } = new();
    public static bool Enabled => AppProfile.IsPersonal && AppSettings.Current.EnableLibraryIndex;
    private readonly SemaphoreSlim _scanGate = new(1);
    private readonly Lazy<LibraryIndex> _index = new(LibraryIndex.ForCurrentProfile);

    internal async Task<(List<ClipItem> Clips, LibraryScanResult Scan)> LoadAsync(
        string root, string[] exclusions, string[]? scopes, CancellationToken token)
    {
        var index = _index.Value;
        await _scanGate.WaitAsync(token);
        Guid id;
        LibraryScanResult scan;
        try
        {
            id = await index.RegisterRootAsync(root, exclusions: exclusions, cancellationToken: token, updateExclusions: true);
            scan = await index.ReconcileAsync(id, new(RelativeDirectory: scopes is { Length: > 0 } ? scopes[0] : null), token);
            if (scopes != null)
                foreach (string scope in scopes.Skip(1))
                {
                    var next = await index.ReconcileAsync(id, new(RelativeDirectory: scope), token);
                    if (!next.Complete) scan = next;
                }
        }
        finally { _scanGate.Release(); }

        var rows = await index.ReadGalleryAsync(id, null, token);
        var clips = await Task.Run(() =>
        {
            var items = new List<ClipItem>(rows.Count);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
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
        return (clips, scan);
    }

    internal async Task<ClipDetails?> ReadAsync(string path)
    {
        if (!Enabled || !WithinConfiguredRoot(path)) return null;
        try
        {
            var row = (await _index.Value.ReadGalleryAsync(null, path)).SingleOrDefault();
            return await Task.Run(() =>
            {
                var file = new FileInfo(path);
                return row != null && file.Exists && ClipMediaStamp.Read(file) == row.Stamp ? row.Details : null;
            });
        }
        catch (Exception ex)
        {
            Log.Write("Could not read indexed clip details: " + ex.GetType().Name);
            return null;
        }
    }

    private static bool WithinConfiguredRoot(string path) => SavedRecordingFolders.ContainsMedia(AppSettings.Current, path);

    public async Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
    {
        if (!Enabled || !WithinConfiguredRoot(target.Path))
            return await LegacyClipAnnotationActions.Instance.SaveDraftAsync(target, draft, token);
        if (target.Details.IndexId == null)
            return new(target.Path, null, "The clip is waiting to be indexed. Reopen it before saving details.");
        try
        {
            await _index.Value.SaveGalleryDetailsAsync(target, draft, token);
            var row = (await _index.Value.ReadGalleryAsync(null, target.Path, token)).SingleOrDefault();
            return row != null ? new(target.Path, row.Details, null)
                : new(target.Path, null, "Saved, but the media is no longer available.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write("Could not save indexed clip details: " + ex.GetType().Name);
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
