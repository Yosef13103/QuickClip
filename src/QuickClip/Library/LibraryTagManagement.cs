using Microsoft.Win32.SafeHandles;
using QuickClip.Gallery;
using static QuickClip.Library.LibraryDatabase;

namespace QuickClip.Library;

internal sealed record LibraryTagUsage(string Name, int ClipCount);

internal sealed partial class LibraryIndex
{
    private const int MaxTagBatch = 256;

    // Scope inventory to the caller's configured, indexed roots. No filesystem scan.
    internal Task<LibraryTagUsage[]> ReadTagUsageAsync(IReadOnlyCollection<Guid> roots,
        CancellationToken token = default)
    {
        var ids = roots.Distinct().Take(MaxTagBatch + 1).ToArray();
        if (ids.Length > MaxTagBatch || ids.Contains(Guid.Empty))
            throw new ArgumentException("Invalid library root selection.", nameof(roots));
        return Run(token, ct =>
        {
            if (ids.Length == 0) return Array.Empty<LibraryTagUsage>();
            using var connection = _database.Open();
            var parameters = ids.Select((id, i) => ($"$root{i}", (object?)Id(id))).ToArray();
            var placeholders = string.Join(",", parameters.Select(p => p.Item1));
            using var query = Command(connection, null, $"""
                SELECT t.name,COUNT(DISTINCT ct.clip_id)
                FROM tags t JOIN clip_tags ct ON ct.tag_id=t.id
                JOIN clips c ON c.id=ct.clip_id JOIN roots r ON r.id=c.root_id
                WHERE t.game_id='' AND c.availability=0 AND r.availability=0 AND c.root_id IN ({placeholders})
                GROUP BY t.id ORDER BY t.name_key;
                """, parameters);
            using var reader = query.ExecuteReader();
            var result = new List<LibraryTagUsage>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                result.Add(new(reader.GetString(0), reader.GetInt32(1)));
            }
            return result.ToArray();
        });
    }

    // All-or-nothing tag-only edits for an explicit selection. Preserve the file,
    // title, game, favorites, bookmarks, drafts and associations. Never fall back
    // to legacy metadata writes or silently truncate existing tags.
    internal Task<int> ApplyTagDeltaAsync(IReadOnlyCollection<ClipAnnotationTarget> selection,
        IReadOnlyCollection<string> add, IReadOnlyCollection<string> remove, CancellationToken token = default,
        bool replaceOnlyPresent = false, bool? hidden = null,
        IReadOnlyCollection<LibraryTagDefinition>? addDefinitions = null,
        IReadOnlyCollection<LibraryTagDefinition>? removeDefinitions = null, bool globalOnly = false)
    {
        var targets = selection.Take(MaxTagBatch + 1).ToArray();
        if (targets.Length > MaxTagBatch ||
            targets.Any(t => t.Details.IndexId is null || t.Details.IndexId == Guid.Empty ||
                t.Details.IndexRevision < 0 || t.Source?.Identity is null) ||
            targets.Select(t => t.Details.IndexId).Distinct().Count() != targets.Length)
            throw new ArgumentException("Select up to 256 distinct verified indexed clips.", nameof(selection));

        static Dictionary<string, string> Names(IReadOnlyCollection<string> values)
        {
            if (values.Count > ClipDetails.MaxTags)
                throw new ArgumentException("Too many tags in one edit.");
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                var name = value?.Trim().Normalize() ?? "";
                if (!ClipDetails.IsValidTag(name))
                    throw new ArgumentException("Invalid tag.");
                names.TryAdd(LibraryFiles.TextKey(name), name);
            }
            return names;
        }

        var additions = Names(add);
        var removals = Names(remove);
        var typedAdds = (addDefinitions ?? []).DistinctBy(t => t.Id).ToArray();
        var typedRemoves = (removeDefinitions ?? []).DistinctBy(t => t.Id).ToArray();
        int addCount = additions.Count + typedAdds.Length, removeCount = removals.Count + typedRemoves.Length;
        if (addCount > ClipDetails.MaxTags || removeCount > ClipDetails.MaxTags ||
            replaceOnlyPresent && (addCount != 1 || removeCount != 1))
            throw new ArgumentException("Invalid number of tag changes.");
        if (additions.Keys.Any(removals.ContainsKey) || typedAdds.Select(t => t.Id).Intersect(typedRemoves.Select(t => t.Id)).Any())
            throw new ArgumentException("A tag cannot be added and removed in the same edit.");

        return Run(token, ct =>
        {
            if (targets.Length == 0 || addCount + removeCount == 0 && hidden == null) return 0;
            var leases = new List<SafeFileHandle>(targets.Length);
            try
            {
                // Hold read-only, identity-checked media leases through commit.
                // Existing replacements requiring write/delete sharing must wait.
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Path.IsPathFullyQualified(target.Path) || ClipLibrary.IsMomentsPath(target.Path) ||
                        !LibraryFiles.EligibleFile(target.Path) ||
                        LibraryFiles.InspectPath(target.Path, false) != ClipAvailability.Available)
                        throw new LibraryConflictException("An original is unavailable. Refresh the selection.");
                    var handle = LibraryFiles.OpenMedia(target.Path);
                    leases.Add(handle);
                    var stamp = LibraryFiles.ReadStamp(handle);
                    if (stamp != target.Source || stamp.Bytes != target.Stamp.Length ||
                        stamp.Created != target.Stamp.Created.Ticks || stamp.Modified != target.Stamp.Modified.Ticks)
                        throw new LibraryConflictException("A selected clip changed. Refresh before editing tags.");
                }

                using var connection = _database.Open();
                using var transaction = connection.BeginTransaction(deferred: false);
                foreach (var definition in typedAdds.Concat(typedRemoves)) VerifyTag(connection, transaction, definition);
                int changed = 0;
                foreach (var target in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    var stamp = target.Source!.Value;
                    string id = Id(target.Details.IndexId!.Value);
                    if (Convert.ToInt64(Scalar(connection, transaction, """
                        SELECT COUNT(*) FROM clips c JOIN roots r ON r.id=c.root_id
                        WHERE c.id=$id AND c.revision=$revision AND c.path_key=$path
                            AND c.availability=0 AND r.availability=0 AND c.file_id=$identity
                            AND c.created_ticks=$created AND c.modified_ticks=$modified AND c.bytes=$bytes;
                        """, ("$id", id), ("$revision", target.Details.IndexRevision),
                        ("$path", LibraryFiles.PathKey(target.Path)), ("$identity", stamp.Identity),
                        ("$created", stamp.Created), ("$modified", stamp.Modified), ("$bytes", stamp.Bytes))) != 1)
                        throw new LibraryConflictException("Clip identity or metadata changed. Refresh before editing tags.");

                    var current = ReadAssignedTags(connection, transaction, id).ToDictionary(t => t.Id);
                    var removed = current.Values.Where(t => typedRemoves.Any(r => r.Id == t.Id) ||
                        (!globalOnly || t.GameId == null) && removals.ContainsKey(LibraryFiles.TextKey(t.Name)))
                        .Select(t => t.Id).ToHashSet();
                    if (replaceOnlyPresent && removed.Count == 0) continue;
                    var next = current.Where(pair => !removed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
                    foreach (var name in additions.Values)
                    {
                        var definition = ResolveTagForClip(connection, transaction, id, name, globalOnly: globalOnly);
                        next[definition.Id] = definition;
                    }
                    var game = ReadClipGameId(connection, transaction, id);
                    foreach (var definition in typedAdds)
                    {
                        if (!definition.AppliesTo(game))
                            throw new LibraryConflictException("A selected tag is not available for every selected recording.");
                        next[definition.Id] = definition;
                    }
                    if (next.Values.Select(t => LibraryFiles.TextKey(t.Name)).Distinct(StringComparer.Ordinal).Count() != next.Count)
                        throw new LibraryConflictException("A recording cannot carry two definitions with the same tag name. Replace the existing tag first.");
                    bool tagsChanged = !current.Keys.ToHashSet().SetEquals(next.Keys);
                    bool visibilityChanged = hidden is bool requested && requested != target.Details.IsHidden;
                    if (!tagsChanged && !visibilityChanged) continue;
                    if (tagsChanged && next.Count > ClipDetails.MaxTags && next.Count >= current.Count)
                        throw new ArgumentException("A selected clip would exceed the tag limit.");
                    foreach (long tagId in current.Keys.Except(next.Keys))
                        Execute(connection, transaction, "DELETE FROM clip_tags WHERE clip_id=$id AND tag_id=$tag;",
                            ("$id", id), ("$tag", tagId));
                    foreach (long tagId in next.Keys.Except(current.Keys))
                        Execute(connection, transaction, "INSERT INTO clip_tags(clip_id,tag_id) VALUES($id,$tag);",
                            ("$id", id), ("$tag", tagId));
                    Execute(connection, transaction, "UPDATE clips SET revision=revision+1,hidden=coalesce($hidden,hidden) WHERE id=$id;",
                        ("$id", id), ("$hidden", hidden));
                    changed++;
                }
                ct.ThrowIfCancellationRequested();
                transaction.Commit();
                return changed;
            }
            finally
            {
                foreach (var handle in leases) handle.Dispose();
            }
        });
    }
}
