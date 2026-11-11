using System.Collections.Immutable;

namespace QuickClip.Discovery;

internal sealed record GameCatalogSettings
{
    public bool AutoScan { get; init; }
    public CatalogRequest Request { get; init; } = new();
    public CatalogResult? Snapshot { get; init; }
    public ImmutableArray<string> OfflineIds { get; init; } = [];
}

internal static class GameCatalogRuntime
{
    private static readonly object Gate = new();
    private static GameCatalogSettings? _settings;
    private static GameCatalogIndex? _index;

    public static CatalogMatch Match(string path, bool foreground)
    {
        if (CatalogPaths.IsDiscordProcess(path)) return new(CatalogMatchKind.Companion);
        if (!AppProfile.IsPersonal) return new(CatalogMatchKind.Unknown);
        var settings = AppSettings.Current.Games;
        if (settings == null) return new(CatalogMatchKind.Unknown);
        lock (Gate)
        {
            if (!ReferenceEquals(settings, _settings))
            {
                var rows = Rows(settings);
                _index = new(new(rows, [], 0, false, default));
                _settings = settings;
            }
            var match = _index!.Match(path, foreground);
            return match.Entry is { } entry && settings.OfflineIds.Contains(entry.Id)
                && match.Kind is CatalogMatchKind.Game or CatalogMatchKind.Candidate
                ? match with { Kind = CatalogMatchKind.Inactive } : match;
        }
    }

    public static ImmutableArray<CatalogEntry> Rows(GameCatalogSettings settings)
    {
        var rules = new Dictionary<string, CatalogRules>(StringComparer.Ordinal);
        foreach (var entry in settings.Request.Overrides) rules[entry.Id] = entry.Rules;
        var roots = settings.Request.GameRoots.Select(root => root.Path).Concat(settings.Request.SteamLibraryRoots).ToArray();
        bool Approved(string path) => roots.Any(root => CatalogPaths.IsWithin(path, root))
            && !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(path, root));
        var discovered = (settings.Snapshot?.Entries ?? []).Where(entry => entry.Source != CatalogSource.Manual)
            .Select(entry => entry with
            {
                InstallDirectories = [.. entry.InstallDirectories.Where(Approved)],
                Executables = [.. entry.Executables.Where(Approved)],
            }).Where(entry => entry.InstallDirectories.Length > 0);
        var manual = settings.Request.ManualEntries.Select(entry => new CatalogEntry(
            "manual:" + entry.Id.ToString("N"), entry.DisplayName, CatalogSource.Manual, [], entry.Executables, new()));
        return [.. discovered.Concat(manual)
            .Select(entry => entry with { Rules = rules.GetValueOrDefault(entry.Id) ?? new() })
            .OrderBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)];
    }

    public static GameCatalogSettings WithScan(GameCatalogSettings settings, CatalogResult scan)
    {
        var previous = settings.Snapshot?.Entries ?? [];
        var seen = scan.Entries.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var retained = previous.Where(entry => entry.Source != CatalogSource.Manual && !seen.Contains(entry.Id)
            && (scan.LimitReached || scan.Issues.Any(issue => entry.InstallDirectories.Any(dir =>
                CatalogPaths.IsWithin(dir, issue.Location) || CatalogPaths.IsWithin(issue.Location, dir)
                || entry.Source == CatalogSource.Steam && CatalogPaths.IsWithin(issue.Location,
                    Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!))))).ToArray();
        return settings with
        {
            Snapshot = scan with { Entries = [.. scan.Entries, .. retained] },
            OfflineIds = [.. retained.Select(entry => entry.Id)],
        };
    }
}
