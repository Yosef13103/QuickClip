namespace QuickClip.Discovery;

internal enum CatalogMatchKind { Unknown, Candidate, Game, Application, Companion, Ignored, Launcher, Inactive, Ambiguous }
internal sealed record CatalogMatch(CatalogMatchKind Kind, CatalogEntry? Entry = null);

/// <summary>Matches a path obtained from an observed process. The caller verifies process and window ownership.</summary>
internal sealed class GameCatalogIndex
{
    private readonly Dictionary<string, List<CatalogEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _launchers = new(StringComparer.Ordinal);

    private readonly HashSet<string> _blocked;

    public GameCatalogIndex(CatalogResult catalog, IEnumerable<string>? blockedExecutables = null)
    {
        _blocked = new((blockedExecutables ?? []).Select(CatalogPaths.LocalPath).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries)
        {
            var launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!entry.Rules.LauncherExecutables.IsDefaultOrEmpty)
                foreach (var raw in entry.Rules.LauncherExecutables)
                    if (CatalogPaths.LocalPath(raw) is { } path) launchers.Add(path);
            _launchers[entry.Id] = launchers;

            foreach (var path in entry.Executables.Select(CatalogPaths.LocalPath).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!_entries.TryGetValue(path, out var matches)) _entries.Add(path, matches = []);
                matches.Add(entry);
            }
        }
    }

    public CatalogMatch Match(string? executable, bool isForeground)
    {
        string? path = CatalogPaths.LocalPath(executable);
        if (path == null || !CatalogPaths.IsExecutable(path)) return new(CatalogMatchKind.Unknown);
        if (_blocked.Contains(path)) return new(CatalogMatchKind.Ignored);
        if (!_entries.TryGetValue(path, out var candidates)) return new(CatalogPaths.DefaultRole(path));

        // Explicit entries outrank discovered paths. Multiple entries of the same precedence
        // require user resolution before the path can drive capture.
        CatalogEntry? match = null;
        bool ambiguous = false;
        foreach (var candidate in candidates)
        {
            if (match == null || (candidate.Source == CatalogSource.Manual && match.Source != CatalogSource.Manual))
            {
                match = candidate;
                ambiguous = false;
            }
            else if ((candidate.Source == CatalogSource.Manual) == (match.Source == CatalogSource.Manual))
                ambiguous = true;
        }

        if (ambiguous) return new(CatalogMatchKind.Ambiguous);
        var entry = match!;
        var rules = entry.Rules;
        if (rules.Inclusion == CatalogInclusion.Exclude) return new(CatalogMatchKind.Ignored, entry);
        if (CatalogPaths.IsDiscordProcess(path)) return new(CatalogPaths.DefaultRole(path), entry);
        if (rules.Classification == AppClassification.Launcher || _launchers[entry.Id].Contains(path))
            return new(CatalogMatchKind.Launcher, entry);
        if (rules.Classification == AppClassification.Application) return new(CatalogMatchKind.Application, entry);
        if (rules.Classification == AppClassification.Companion) return new(CatalogMatchKind.Companion, entry);
        if (rules.Classification == AppClassification.Automatic &&
            CatalogPaths.DefaultRole(path) is var role && role != CatalogMatchKind.Unknown)
            return new(role, entry);
        if (rules.Activation == CatalogActivation.ForegroundOnly && !isForeground) return new(CatalogMatchKind.Inactive, entry);
        bool game = rules.Classification == AppClassification.Game || entry.Source == CatalogSource.Manual;
        return new(game ? CatalogMatchKind.Game : CatalogMatchKind.Candidate, entry);
    }
}
