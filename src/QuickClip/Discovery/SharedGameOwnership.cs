using System.Collections.Immutable;

namespace QuickClip.Discovery;

/// <summary>Resolve ambiguous exact paths using the existing persisted attachment rules.
/// Never merge unrelated title metadata or implicitly override denials.</summary>
internal static class SharedGameOwnership
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

    internal static string[] Conflicts(CatalogEntry entry, GameCatalogIndex index) =>
        entry.Executables.Where(path => index.Match(path, true).Kind == CatalogMatchKind.Ambiguous)
            .Distinct(Paths).OrderBy(path => path, Paths).ToArray();

    internal static CatalogEntry[] Claimants(IEnumerable<CatalogEntry> entries, string path) =>
        entries.Where(entry => entry.Executables.Contains(path, Paths))
            .OrderBy(entry => entry.DisplayName, Paths).ThenBy(entry => entry.Id, StringComparer.Ordinal).ToArray();

    internal static GameCatalogSettings? Choose(GameCatalogSettings state, IReadOnlyList<CatalogEntry> entries,
        string path, string ownerId)
    {
        var blocked = CatalogClaims.BlockedExecutables(state);
        var index = new GameCatalogIndex(new([.. entries], [], 0, false, default), blocked);
        if (index.Match(path, true).Kind != CatalogMatchKind.Ambiguous ||
            CatalogPaths.DefaultRole(path) != CatalogMatchKind.Unknown || CatalogPaths.IsProtectedExecutable(path) ||
            CatalogPaths.IsJavaRuntime(path)) return null;
        var claimants = Claimants(entries, path);
        var owner = claimants.SingleOrDefault(entry => entry.Id == ownerId);
        if (owner == null || owner.IsUnverified || owner.UnverifiedExecutables.Contains(path, Paths) ||
            owner.DetectedLaunchers.Contains(path, Paths)) return null;

        // Transfer only this path, including any alias overrides. Leave other
        // executable roles, metadata, exclusions and unrelated paths untouched.
        var ids = claimants.SelectMany(entry => entry.AliasIds.Prepend(entry.Id)).ToHashSet(StringComparer.Ordinal);
        var overrides = state.Request.Overrides.Select(item => ids.Contains(item.Id)
            ? item with { Rules = item.Rules with
                { AttachedExecutables = item.Rules.AttachedExecutables.RemoveAll(value => Paths.Equals(value, path)) } }
            : item).ToImmutableArray();
        var existing = overrides.LastOrDefault(item => item.Id == owner.Id);
        var selected = (existing?.Rules ?? owner.Rules) with
        {
            AttachedExecutables = [.. (existing?.Rules ?? owner.Rules).AttachedExecutables
                .Where(value => !Paths.Equals(value, path)).Append(path).Distinct(Paths)],
        };
        overrides = overrides.RemoveAll(item => item.Id == owner.Id)
            .Add(new(owner.Id, selected) { DeniedExecutables = existing?.DeniedExecutables ?? [] });
        var updated = state with { Request = state.Request with { Overrides = overrides } };
        // Confirm using the same projected catalog/runtime ranking, not a
        // separate permissive matching rule.
        var result = new GameCatalogIndex(new(GameCatalogRuntime.Rows(updated), [], 0, false, default),
            CatalogClaims.BlockedExecutables(updated)).Match(path, true);
        return result.Kind == CatalogMatchKind.Game && result.Entry?.Id == owner.Id ? updated : null;
    }
}