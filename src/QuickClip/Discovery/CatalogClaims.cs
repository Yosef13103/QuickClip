using System.Collections.Immutable;

namespace QuickClip.Discovery;

internal sealed record CatalogScope(CatalogSource Kind, string? Root = null, bool IsCollection = false)
{
    public string Key => $"{Kind}:{(IsCollection ? "collection:" : "")}{Root?.ToUpperInvariant()}";

    public static IEnumerable<CatalogScope> Configured(GameCatalogSettings settings)
    {
        var request = settings.Request;
        var scopes = request.GameRoots.Select(root => new CatalogScope(CatalogSource.Folder, Normalize(root.Path), root.IsCollection))
            .Concat(request.SteamLibraryRoots.Select(root => new CatalogScope(CatalogSource.Steam, Normalize(root))))
            .Where(scope => scope.Root != null);
        if (settings.DiscoverSteam ?? settings.AutoScan) scopes = scopes.Append(new(CatalogSource.Steam));
        if (settings.DiscoverEpic ?? settings.AutoScan) scopes = scopes.Append(new(CatalogSource.Epic));
        if (settings.DiscoverGog) scopes = scopes.Append(new(CatalogSource.Gog));
        return scopes.DistinctBy(scope => scope.Key);
    }

    public static IEnumerable<CatalogScope> Enabled(GameCatalogSettings settings) =>
        Configured(settings).Where(scope => !settings.DisabledSourceKeys.Contains(scope.Key, StringComparer.Ordinal) &&
            (scope.Root == null || !Exclusions(settings.Request).Any(path => CatalogPaths.IsWithin(scope.Root, path))));

    public CatalogRequest Request(CatalogRequest settings) => new()
    {
        GameRoots = Kind == CatalogSource.Folder && Root != null ? [new(Root, IsCollection)] : [],
        SteamLibraryRoots = Kind == CatalogSource.Steam && Root != null ? [Root] : [],
        DiscoverSteam = Kind == CatalogSource.Steam && Root == null,
        DiscoverEpic = Kind == CatalogSource.Epic,
        DiscoverGog = Kind == CatalogSource.Gog,
        ExcludedDirectories = [.. Exclusions(settings).Where(path => Root == null ||
            CatalogPaths.IsWithin(path, Root) || CatalogPaths.IsWithin(Root, path))],
        MaxEntries = settings.MaxEntries,
        MaxDepth = settings.MaxDepth == 8 ? null : settings.MaxDepth,
    };

    public static string Signature(CatalogRequest request) => $"{request.MaxEntries}/{request.MaxDepth}/" +
        string.Join('\0', request.ExcludedDirectories.Select(path => path.ToUpperInvariant()));

    public static IEnumerable<string> Exclusions(CatalogRequest request) => request.ExcludedDirectories
        .Select(Normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);

    private static string? Normalize(string path) => CatalogPaths.LocalPath(path) is { Length: > 3 } normal ? normal : null;
}

internal sealed record CatalogSourceClaim(CatalogScope Scope, CatalogResult Result, bool Verified, string Signature);
internal sealed record CatalogInstallAlias(string Path, string Id);

/// <summary>Projects source claims into catalog rows. Only a complete source result can add live executable evidence.</summary>
internal static class CatalogClaims
{
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private sealed record Contribution(CatalogEntry Entry, bool Verified);

    public static GameCatalogSettings Start(GameCatalogSettings settings) => settings.SourceClaims != null ? settings :
        settings with { SourceClaims = [], LegacyEntries = [.. (settings.Snapshot?.Entries ?? []).Where(entry => entry.Source != CatalogSource.Manual)] };

    public static GameCatalogSettings Reconfigure(GameCatalogSettings settings)
    {
        if (settings.SourceClaims is not { } claims) return settings;
        var enabled = CatalogScope.Enabled(settings).ToDictionary(scope => scope.Key, StringComparer.Ordinal);
        var configured = CatalogScope.Configured(settings).Select(scope => scope.Key).ToHashSet(StringComparer.Ordinal);
        var next = claims.Where(claim => claim.Scope.Root == null || configured.Contains(claim.Scope.Key))
            .Select(claim => claim.Verified && (!enabled.TryGetValue(claim.Scope.Key, out var scope) ||
                claim.Signature != CatalogScope.Signature(scope.Request(settings.Request)))
                ? claim with { Verified = false } : claim);
        return claims.SequenceEqual(next) ? settings : settings with { SourceClaims = [.. next] };
    }

    public static bool Complete(CatalogResult result) => !result.InProgress && !result.LimitReached &&
        !result.HasUnreportedFailures && !result.SourceUnavailable &&
        result.Issues.All(issue => issue.Kind == CatalogIssueKind.ReparsePoint);

    public static GameCatalogSettings Apply(GameCatalogSettings settings, CatalogScope scope, CatalogResult result, bool preview)
    {
        settings = Start(settings);
        string signature = CatalogScope.Signature(scope.Request(settings.Request));
        var previous = settings.SourceClaims!.Value.FirstOrDefault(claim => claim.Scope.Key == scope.Key);
        bool complete = Complete(result);
        if (preview)
            return settings with { ScanPreview = new(scope, result, false, signature) };

        // Keep the previous complete inventory on failure, including paths the current scan did not reach.
        if (!complete && previous != null)
            result = result with
            {
                Entries = [.. previous.Result.Entries.Concat(result.Entries).GroupBy(entry => entry.Id)
                    .Select(group => MergeUnverified(group))],
                SteamLibraries = [.. previous.Result.SteamLibraries.Concat(result.SteamLibraries).Distinct(Paths)],
                EpicManifestDirectory = result.EpicManifestDirectory ?? previous.Result.EpicManifestDirectory,
            };
        var claims = settings.SourceClaims.Value.RemoveAll(claim => claim.Scope.Key == scope.Key)
            .Add(new(scope, result, complete, signature));
        var enabled = CatalogScope.Enabled(settings).Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
        var legacy = settings.LegacyEntries.Where(entry => !complete || !CoveredByCompleteClaims(entry, claims, enabled));
        // Record only proven provider identities. Exact shared install paths can retain an override
        // when a provider is disabled or removed and an independently verified folder remains.
        var aliases = complete
            ? settings.InstallAliases.Concat(result.Entries.Where(entry => entry.Source is CatalogSource.Steam or CatalogSource.Epic or CatalogSource.Gog)
                .SelectMany(entry => entry.InstallDirectories.Select(path => new CatalogInstallAlias(path, entry.Id))))
            : settings.InstallAliases;
        var referenced = claims.SelectMany(claim => claim.Result.Entries).SelectMany(entry => entry.InstallDirectories).ToHashSet(Paths);
        var overridden = settings.Request.Overrides.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        return settings with
        {
            SourceClaims = claims,
            LegacyEntries = [.. legacy],
            InstallAliases = [.. aliases.DistinctBy(alias => (alias.Path.ToUpperInvariant(), alias.Id))
                .Where(alias => referenced.Contains(alias.Path) || overridden.Contains(alias.Id))],
            ScanPreview = null,
        };
    }

    private static bool CoveredByCompleteClaims(CatalogEntry entry, ImmutableArray<CatalogSourceClaim> claims, HashSet<string> enabled)
    {
        if (entry.InstallDirectories.IsEmpty) return true;
        // The old snapshot can't say which overlapping source found the path. An authoritative
        // scan of an applicable scope can retire the old display-only copy.
        return entry.InstallDirectories.All(path => claims.Any(claim => claim.Verified && enabled.Contains(claim.Scope.Key) &&
            claim.Scope.Kind == entry.Source && (claim.Scope.Root is { } root ? CatalogPaths.IsWithin(path, root) :
                claim.Scope.Kind != CatalogSource.Steam || claim.Result.SteamLibraries.Any(root => CatalogPaths.IsWithin(path, root)))));
    }

    public static GameCatalogSettings Summarize(GameCatalogSettings settings, CatalogResult activity, bool finished,
        IReadOnlySet<string> refreshed)
    {
        var rows = Rows(settings);
        var enabled = CatalogScope.Enabled(settings).Select(scope => scope.Key).ToHashSet(StringComparer.Ordinal);
        var claims = settings.SourceClaims!.Value.Where(claim => enabled.Contains(claim.Scope.Key)).ToArray();
        var discovered = claims.Where(claim => claim.Verified && refreshed.Contains(claim.Scope.Key))
            .SelectMany(claim => claim.Result.Entries).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        return settings with
        {
            Snapshot = activity with
            {
                Entries = [.. rows.Where(row => row.Source != CatalogSource.Manual)],
                SteamLibraries = [.. claims.SelectMany(claim => claim.Result.SteamLibraries).Distinct(Paths)],
                EpicManifestDirectory = claims.Select(claim => claim.Result.EpicManifestDirectory).FirstOrDefault(path => path != null),
                InProgress = !finished,
                CompletedAt = finished ? activity.CompletedAt : settings.Snapshot?.CompletedAt ?? default,
            },
            DiscoveredCount = rows.Count(row => row.Source != CatalogSource.Manual &&
                (discovered.Contains(row.Id) || row.AliasIds.Any(discovered.Contains))),
            RetainedIds = [.. rows.Where(row => row.IsUnverified || !row.UnverifiedExecutables.IsEmpty).Select(row => row.Id)],
            OfflineIds = [.. rows.Where(row => row.IsUnverified).Select(row => row.Id)],
        };
    }

    public static ImmutableArray<CatalogEntry> Rows(GameCatalogSettings settings)
    {
        var scopes = CatalogScope.Enabled(settings).ToDictionary(scope => scope.Key, StringComparer.Ordinal);
        var exclusions = CatalogScope.Exclusions(settings.Request).ToArray();
        bool Allowed(string path) => CatalogPaths.LocalPath(path) != null && !exclusions.Any(root => CatalogPaths.IsWithin(path, root));
        var contributions = new List<Contribution>();
        foreach (var claim in settings.SourceClaims!.Value.Concat(settings.ScanPreview is { } preview ? [preview] : []))
        {
            if (!scopes.TryGetValue(claim.Scope.Key, out var scope)) continue;
            if (claim.Result.EpicManifestDirectory is { } epic && !Allowed(epic)) continue;
            bool verified = claim.Verified && claim.Signature == CatalogScope.Signature(scope.Request(settings.Request));
            foreach (var entry in claim.Result.Entries)
            {
                if (entry.Source != scope.Kind) continue;
                var installs = entry.InstallDirectories.Where(path => Allowed(path) &&
                    (scope.Root == null || CatalogPaths.IsWithin(path, scope.Root))).ToImmutableArray();
                if (installs.IsEmpty) continue;
                bool InInstall(string path) => Allowed(path) && installs.Any(root => CatalogPaths.IsWithin(path, root));
                contributions.Add(new(entry with
                {
                    InstallDirectories = installs,
                    Executables = [.. entry.Executables.Where(InInstall)],
                    RendererExecutables = entry.RendererExecutables is { } renderers ? [.. renderers.Where(InInstall)] : null,
                    DetectedLaunchers = [.. entry.DetectedLaunchers.Where(InInstall)],
                }, verified));
            }
        }
        foreach (var entry in settings.LegacyEntries.Where(entry => entry.Source != CatalogSource.Manual))
        {
            var installs = entry.InstallDirectories.Where(path => Allowed(path) && scopes.Values.Any(scope =>
                scope.Kind == entry.Source && (scope.Root == null || CatalogPaths.IsWithin(path, scope.Root)))).ToImmutableArray();
            if (!installs.IsEmpty)
                contributions.Add(new(entry with
                {
                    InstallDirectories = installs,
                    Executables = [.. entry.Executables.Where(path => Allowed(path) && installs.Any(root => CatalogPaths.IsWithin(path, root)))],
                }, false));
        }

        var rules = settings.Request.Overrides.GroupBy(rule => rule.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Rules, StringComparer.Ordinal);
        var aliases = settings.InstallAliases.ToLookup(alias => alias.Path, alias => alias.Id, Paths);
        var providers = contributions.Where(item => item.Entry.Source != CatalogSource.Folder)
            .SelectMany(item => item.Entry.InstallDirectories.Select(path => (path, item.Entry.Id)))
            .ToLookup(item => item.path, item => item.Id, Paths);
        string Identity(Contribution item)
        {
            if (item.Entry.Source != CatalogSource.Folder || item.Entry.InstallDirectories is not [var path]) return item.Entry.Id;
            // Two different provider titles claiming the same directory are still ambiguous.
            var ids = providers[path].Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Length == 0) ids = aliases[path].Distinct(StringComparer.Ordinal).ToArray();
            return ids is [var id] ? id : item.Entry.Id;
        }

        bool hasFolderOverrides = rules.Keys.Any(id => id.StartsWith("folder:", StringComparison.Ordinal));
        var rows = contributions.GroupBy(Identity, StringComparer.Ordinal)
            .Select(group => Project(group.Key, group.ToArray(), rules, hasFolderOverrides));
        var manual = settings.Request.ManualEntries.Select(entry =>
        {
            string id = "manual:" + entry.Id.ToString("N");
            return new CatalogEntry(id, entry.DisplayName, CatalogSource.Manual, [],
                [.. entry.Executables.Where(Allowed)], rules.GetValueOrDefault(id) ?? new());
        });
        return [.. rows.Concat(manual).OrderBy(row => row.DisplayName, Paths).ThenBy(row => row.Id, StringComparer.Ordinal)];
    }

    private static CatalogEntry Project(string id, Contribution[] parts, Dictionary<string, CatalogRules> rules, bool hasFolderOverrides)
    {
        var live = parts.Where(part => part.Verified).Select(part => part.Entry).ToArray();
        var all = parts.Select(part => part.Entry).ToArray();
        var preferred = live.FirstOrDefault(entry => entry.Id == id) ?? live.FirstOrDefault() ??
            all.FirstOrDefault(entry => entry.Id == id) ?? all[0];
        var matchingFolderOverrides = hasFolderOverrides ? all.SelectMany(entry => entry.InstallDirectories).Distinct(Paths)
            .Select(CatalogPaths.FolderId).Where(rules.ContainsKey) : [];
        string[] ids = [.. all.Select(entry => entry.Id).Prepend(id).Concat(matchingFolderOverrides).Distinct(StringComparer.Ordinal)];
        var overrides = ids.Select(key => rules.GetValueOrDefault(key)).OfType<CatalogRules>().ToArray();
        var rule = overrides.FirstOrDefault() ?? new();
        rule = rule with
        {
            Inclusion = overrides.Any(item => item.Inclusion == CatalogInclusion.Exclude) ? CatalogInclusion.Exclude : rule.Inclusion,
            Classification = overrides.Select(item => item.Classification).FirstOrDefault(value =>
                value is AppClassification.Launcher or AppClassification.Application or AppClassification.Companion, rule.Classification),
            LauncherExecutables = [.. overrides.SelectMany(item => item.LauncherExecutables).Distinct(Paths)],
            ApprovedExecutables = [.. overrides.SelectMany(item => item.ApprovedExecutables).Distinct(Paths)],
        };
        var livePaths = live.SelectMany(entry => entry.Executables).ToHashSet(Paths);
        var executables = preferred.Executables.Concat(all.SelectMany(entry => entry.Executables)).Distinct(Paths).ToImmutableArray();
        return preferred with
        {
            Id = id,
            Rules = rule,
            AliasIds = [.. ids.Where(key => key != id)],
            InstallDirectories = [.. all.SelectMany(entry => entry.InstallDirectories).Distinct(Paths)],
            Executables = executables,
            UnverifiedExecutables = [.. executables.Where(path => !livePaths.Contains(path))],
            IsUnverified = live.Length == 0 || (livePaths.Count == 0 && !executables.IsEmpty),
            RendererExecutables = [.. live.SelectMany<CatalogEntry, string>(entry => entry.Source == CatalogSource.Steam ? entry.Executables :
                entry.RendererExecutables ?? (entry.Source == CatalogSource.Epic ? entry.Executables : [])).Distinct(Paths)],
            DetectedLaunchers = [.. all.SelectMany(entry => entry.DetectedLaunchers).Distinct(Paths)],
        };
    }

    private static CatalogEntry MergeUnverified(IEnumerable<CatalogEntry> entries)
    {
        var items = entries.ToArray();
        return items[^1] with
        {
            InstallDirectories = [.. items.SelectMany(entry => entry.InstallDirectories).Distinct(Paths)],
            Executables = [.. items.SelectMany(entry => entry.Executables).Distinct(Paths)],
            DetectedLaunchers = [.. items.SelectMany(entry => entry.DetectedLaunchers).Distinct(Paths)],
        };
    }
}
