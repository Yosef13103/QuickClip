using System.Collections.Immutable;

namespace QuickClip.Discovery;

internal sealed record GameCatalogSettings
{
    public bool AutoScan { get; init; } = true;
    public CatalogRequest Request { get; init; } = new();
    public CatalogResult? Snapshot { get; init; }
    public ImmutableArray<string> BlockedExecutables { get; init; } = [];
    public ImmutableArray<string> OfflineIds { get; init; } = [];
    public int? DiscoveredCount { get; init; }
    public ImmutableArray<string> RetainedIds { get; init; } = [];
}

internal static class GameCatalogRuntime
{
    internal static GameCatalogSettings RestoreSavedScan(GameCatalogSettings settings)
    {
        if (settings.Snapshot?.InProgress != true) return settings;
        return settings with
        {
            OfflineIds = [.. settings.OfflineIds.Concat(settings.RetainedIds).Distinct(StringComparer.Ordinal)],
        };
    }

    public static GameDiscoveryCoordinator Discovery { get; } = new(
        () => AppSettings.Current.Games ?? new(),
        (state, persist) =>
        {
            AppSettings.Current.Games = state;
            if (persist) AppSettings.Current.Save();
        });

    private static readonly object Gate = new();
    private static GameCatalogSettings? _settings;
    private static GameCatalogIndex? _index;

    public static CatalogMatch Match(string path, bool foreground)
    {
        if (!AppProfile.IsPersonal) return new(CatalogPaths.IsDiscordProcess(path)
            ? CatalogMatchKind.Companion : CatalogMatchKind.Unknown);
        var settings = AppSettings.Current.Games;
        if (settings == null) return new(CatalogPaths.DefaultRole(path));
        if (settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(path, root)))
            return new(CatalogMatchKind.Ignored);
        lock (Gate)
        {
            if (!ReferenceEquals(settings, _settings))
            {
                var rows = Rows(settings);
                _index = new(new(rows, [], 0, false, default), settings.BlockedExecutables);
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
        var roots = settings.Request.GameRoots.Select(root => root.Path).Concat(settings.Request.SteamLibraryRoots)
            .Concat(settings.Snapshot?.SteamLibraries ?? []).ToArray();
        bool Approved(CatalogEntry entry, string path) =>
            (entry.Source == CatalogSource.Epic
                ? settings.Snapshot?.EpicManifestDirectory is { } epic &&
                !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(epic, root)) &&
                entry.InstallDirectories.Any(root => CatalogPaths.IsWithin(path, root))
                : roots.Any(root => CatalogPaths.IsWithin(path, root))) &&
            !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(path, root));
        var discovered = (settings.Snapshot?.Entries ?? []).Where(entry => entry.Source != CatalogSource.Manual)
            .Select(entry => entry with
            {
                InstallDirectories = [.. entry.InstallDirectories.Where(path => Approved(entry, path))],
                Executables = [.. entry.Executables.Where(path => Approved(entry, path) &&
                    CatalogPaths.DefaultRole(path) == CatalogMatchKind.Unknown)],
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
        bool HasSourceIssue(CatalogEntry entry) =>
            scan.Issues.Any(issue =>
                entry.Source == CatalogSource.Epic && scan.EpicManifestDirectory is { } manifestDirectory &&
                CatalogPaths.IsWithin(issue.Location, manifestDirectory) ||
                entry.InstallDirectories.Any(dir =>
                CatalogPaths.IsWithin(dir, issue.Location) || CatalogPaths.IsWithin(issue.Location, dir) ||
                entry.Source == CatalogSource.Steam && CatalogPaths.IsWithin(issue.Location,
                    Path.GetDirectoryName(Path.GetDirectoryName(dir)!)!)));
        bool MayNeedRetention(CatalogEntry entry) => HasSourceIssue(entry) || scan.HasUnreportedFailures ||
            scan.LimitReached && scan.Issues.IsEmpty;
        var retained = previous.Where(entry => entry.Source != CatalogSource.Manual &&
            (scan.InProgress || MayNeedRetention(entry))).ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        var offline = new HashSet<string>(StringComparer.Ordinal);
        var previouslyOffline = settings.OfflineIds.ToHashSet(StringComparer.Ordinal);
        var remembered = new HashSet<string>(StringComparer.Ordinal);
        var merged = scan.Entries.Select(entry => retained.Remove(entry.Id, out var prior)
            ? Merge(entry, prior)
            : entry).ToArray();
        foreach (var entry in scan.Entries)
            if (entry.Source != CatalogSource.Manual && HasSourceIssue(entry)) offline.Add(entry.Id);
        foreach (var (id, entry) in retained)
        {
            remembered.Add(id);
            if (!scan.InProgress || previouslyOffline.Contains(id) || MayNeedRetention(entry)) offline.Add(id);
        }
        var entries = merged.Concat(retained.Values).ToArray();
        return settings with
        {
            Snapshot = scan with
            {
                Entries = [.. entries],
                CompletedAt = scan.InProgress ? settings.Snapshot?.CompletedAt ?? default : scan.CompletedAt,
                SteamLibraries = [.. scan.SteamLibraries.Concat((scan.InProgress || scan.LimitReached || scan.Issues.Length > 0)
                    ? settings.Snapshot?.SteamLibraries ?? [] : []).Distinct(StringComparer.OrdinalIgnoreCase)],
                EpicManifestDirectory = scan.EpicManifestDirectory ?? (scan.InProgress || scan.LimitReached
                    ? settings.Snapshot?.EpicManifestDirectory : null),
            },
            OfflineIds = [.. offline.Order(StringComparer.Ordinal)],
            RetainedIds = [.. remembered.Order(StringComparer.Ordinal)],
            DiscoveredCount = scan.Entries.Count(entry => entry.Source != CatalogSource.Manual),
        };

        CatalogEntry Merge(CatalogEntry entry, CatalogEntry prior)
        {
            var comparer = StringComparer.OrdinalIgnoreCase;
            if (prior.Executables.Except(entry.Executables, comparer).Any() ||
                prior.InstallDirectories.Except(entry.InstallDirectories, comparer).Any())
            {
                remembered.Add(entry.Id);
                // Previously visible paths stay listed but a mixed row needs
                // revalidation before any of those paths can trigger capture.
                offline.Add(entry.Id);
            }
            return entry with
            {
                InstallDirectories = [.. entry.InstallDirectories.Concat(prior.InstallDirectories).Distinct(comparer)],
                Executables = [.. entry.Executables.Concat(prior.Executables).Distinct(comparer)],
            };
        }
    }
}
