namespace QuickClip.Discovery;

internal enum RecordingProfileRowKind { AllGames, Game, Separator }

internal sealed record RecordingProfileRow(RecordingProfileRowKind Kind, string Label,
    string? GameId = null, string? ProfileKey = null, GameRecordingPolicy? Policy = null, bool Conflict = false)
{
    // Tags from retired preview feature remain readable but do not count as active overrides.
    public bool HasOverrides => Policy != null && (Policy with { Tags = null }) != new GameRecordingPolicy();
    public bool Enabled => Kind != RecordingProfileRowKind.Separator && !Conflict;
    public override string ToString() => Label;
}

// Editing-only catalog projection. Never sets CatalogRules, display permission,
// recording master or live recorder state. Preserve unattached/orphaned profiles.
internal static class RecordingProfileSelection
{
    internal static RecordingProfileRow[] Build(IEnumerable<CatalogEntry> catalog, GameProfileDocument document)
    {
        document.Validate();
        var games = catalog.Where(entry => entry.Rules.Inclusion != CatalogInclusion.Exclude &&
            entry.Rules.Classification is not (AppClassification.Application or AppClassification.Companion or AppClassification.Launcher))
            .DistinctBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        var duplicates = games.GroupBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var rows = games.Select(entry =>
        {
            var candidates = entry.AliasIds.Prepend(entry.Id)
                .Concat(entry.Rules.ProfileId is { } id ? new[] { id } : Array.Empty<string>())
                .Distinct(StringComparer.Ordinal).ToArray();
            var existing = candidates.Where(document.Profiles.ContainsKey).ToArray();
            // A catalog merge must not silently pick one of two distinct saved policies.
            string key = existing.Length == 1 ? existing[0] : entry.Rules.ProfileId ?? entry.Id;
            bool conflict = existing.Length > 1 || !GameRecordingPolicy.IsId(key, 128);
            string label = duplicates.Contains(entry.DisplayName)
                ? $"{entry.DisplayName} ({entry.Id})" : entry.DisplayName;
            return new RecordingProfileRow(RecordingProfileRowKind.Game, label, entry.Id, key,
                conflict ? null : document.Profiles.GetValueOrDefault(key), conflict);
        }).ToArray();

        // Avoid editing a shared explicit profile as if it belonged to only one game.
        var sharedKeys = rows.Where(row => row.ProfileKey != null).GroupBy(row => row.ProfileKey!)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        rows = rows.Select(row => row.ProfileKey != null && sharedKeys.Contains(row.ProfileKey)
            ? row with { Conflict = true } : row).ToArray();

        RecordingProfileRow[] Order(IEnumerable<RecordingProfileRow> items) => items
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.GameId, StringComparer.Ordinal).ToArray();
        var customized = Order(rows.Where(row => row.HasOverrides || row.Conflict));
        var inherited = Order(rows.Where(row => !row.HasOverrides && !row.Conflict));
        return [new(RecordingProfileRowKind.AllGames, "All Games"), .. customized,
            .. (customized.Length > 0 && inherited.Length > 0
                ? new[] { new RecordingProfileRow(RecordingProfileRowKind.Separator, "────────") } : []),
            .. inherited];
    }

    internal static GameProfileDocument Change(GameProfileDocument document, RecordingProfileRow row,
        GameRecordingPolicy policy)
    {
        if (row.Kind != RecordingProfileRowKind.Game || !row.Enabled || row.ProfileKey is not { } key)
            throw new ArgumentException("Choose an unambiguous game profile.", nameof(row));
        document.Validate();
        if (!Equals(document.Profiles.GetValueOrDefault(key), row.Policy))
            throw new InvalidOperationException("The profile changed. Refresh before editing.");
        policy.Validate();
        var updated = document with
        {
            Profiles = policy == new GameRecordingPolicy()
                ? document.Profiles.Remove(key) : document.Profiles.SetItem(key, policy),
        };
        updated.Validate();
        return updated;
    }
}
