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
    public bool DiscoverGog { get; init; }
    public bool? DiscoverSteam { get; init; }
    public bool? DiscoverEpic { get; init; }
    public ImmutableArray<CatalogSource> EnabledLaunchers { get; init; } = [];
    public ImmutableArray<string> DisabledSourceKeys { get; init; } = [];
    public ImmutableArray<CatalogSourceClaim>? SourceClaims { get; init; }
    public ImmutableArray<CatalogEntry> LegacyEntries { get; init; } = [];
    public ImmutableArray<CatalogInstallAlias> InstallAliases { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    internal CatalogSourceClaim? ScanPreview { get; init; }
}

internal sealed class GameCatalogIndexCache
{
    private GameCatalogSettings? _settings;
    private GameCatalogIndex? _index;

    public GameCatalogIndex Get(GameCatalogSettings state)
    {
        if (ReferenceEquals(state, _settings)) return _index!;
        if (_settings != null && HasSameCaptureInputs(_settings, state))
        {
            _settings = state;
            return _index!;
        }

        var verified = state.ScanPreview;
        var input = CatalogClaims.Start(state) with
        {
            ScanPreview = verified is { VerifiedInstalls.IsEmpty: false }
                ? verified with { Result = verified.Result with { Entries = [] } } : null,
        };
        var rows = CatalogClaims.Rows(input);
        var index = new GameCatalogIndex(new(rows, [], 0, false, default), CatalogClaims.BlockedExecutables(input));
        _settings = state;
        return _index = index;
    }

    private static bool HasSameCaptureInputs(GameCatalogSettings prior, GameCatalogSettings current) =>
        prior.Request == current.Request && prior.AutoScan == current.AutoScan &&
        prior.DiscoverSteam == current.DiscoverSteam && prior.DiscoverEpic == current.DiscoverEpic &&
        prior.DiscoverGog == current.DiscoverGog && prior.EnabledLaunchers == current.EnabledLaunchers &&
        prior.DisabledSourceKeys == current.DisabledSourceKeys && prior.BlockedExecutables == current.BlockedExecutables &&
        prior.SourceClaims == current.SourceClaims && prior.LegacyEntries == current.LegacyEntries &&
        prior.InstallAliases == current.InstallAliases &&
        (current.SourceClaims != null || ReferenceEquals(prior.Snapshot, current.Snapshot)) &&
        PreviewEvidence(prior.ScanPreview) == PreviewEvidence(current.ScanPreview);

    private static (CatalogScope?, string?, ImmutableArray<CatalogEntry>) PreviewEvidence(CatalogSourceClaim? preview) =>
        preview is { VerifiedInstalls.IsEmpty: false } ? (preview.Scope, preview.Signature, preview.VerifiedInstalls) : default;
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
    private static readonly GameCatalogIndexCache Index = new();

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
            return Index.Get(settings).Match(path, foreground);
        }
    }

    public static ImmutableArray<CatalogEntry> Rows(GameCatalogSettings settings)
    {
        if (settings.SourceClaims is not null) return CatalogClaims.Rows(settings);
        var rules = new Dictionary<string, CatalogRules>(StringComparer.Ordinal);
        foreach (var entry in settings.Request.Overrides) rules[entry.Id] = entry.Rules;
        var folderRoots = settings.Request.GameRoots.Select(root => CatalogPaths.LocalPath(root.Path)).OfType<string>().ToArray();
        var steamRoots = settings.Request.SteamLibraryRoots.Concat(settings.Snapshot?.SteamLibraries ?? [])
            .Select(CatalogPaths.LocalPath).OfType<string>().ToArray();
        bool Approved(CatalogEntry entry, string path) =>
            (entry.Source switch
            {
                CatalogSource.Epic => settings.Snapshot?.EpicManifestDirectory is { } epic &&
                    !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(epic, root)) &&
                    entry.InstallDirectories.Any(root => CatalogPaths.IsWithin(path, root)),
                CatalogSource.Folder => folderRoots.Any(root => CatalogPaths.IsWithin(path, root)),
                CatalogSource.Steam => steamRoots.Any(root => CatalogPaths.IsWithin(path, root)),
                _ => false,
            }) &&
            !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(path, root));
        var discovered = (settings.Snapshot?.Entries ?? []).Where(entry => entry.Source != CatalogSource.Manual && !CatalogPaths.IsRedistributable(entry))
            .Select(entry => entry with
            {
                InstallDirectories = [.. entry.InstallDirectories.Where(path => Approved(entry, path))],
                Executables = [.. entry.Executables.Where(path => Approved(entry, path) &&
                    CatalogPaths.DefaultRole(path) is CatalogMatchKind.Unknown or CatalogMatchKind.Launcher)],
                RendererExecutables = entry.RendererExecutables is { } renderers
                    ? [.. renderers.Where(path => Approved(entry, path))]
                    : null,
                DetectedLaunchers = [.. entry.DetectedLaunchers.Where(path => Approved(entry, path))],
            }).Where(entry => entry.InstallDirectories.Length > 0);
        var manual = settings.Request.ManualEntries.Select(entry => new CatalogEntry(
            "manual:" + entry.Id.ToString("N"), entry.DisplayName, CatalogSource.Manual, [], entry.Executables, new()));
        return [.. discovered.Concat(manual)
            .Select(entry =>
            {
                var rule = rules.GetValueOrDefault(entry.Id) ?? new();
                IEnumerable<string> attached = entry.Source == CatalogSource.Manual ? [] :
                    rule.AttachedExecutables.Where(path => !CatalogPaths.IsProtectedExecutable(path) &&
                        !settings.Request.ExcludedDirectories.Any(root => CatalogPaths.IsWithin(path, root)));
                return entry with { Rules = rule, Executables = [.. entry.Executables.Union(attached, StringComparer.OrdinalIgnoreCase)] };
            })
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
                DetectedLaunchers = [.. entry.DetectedLaunchers.Concat(prior.DetectedLaunchers).Distinct(comparer)],
            };
        }
    }
}
