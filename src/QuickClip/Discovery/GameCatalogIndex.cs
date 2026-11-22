namespace QuickClip.Discovery;

internal enum CatalogMatchKind { Unknown, Candidate, Game, Application, Companion, Ignored, Launcher, Inactive, Ambiguous }
internal sealed record CatalogMatch(CatalogMatchKind Kind, CatalogEntry? Entry = null);

/// <summary>Matches a path obtained from an observed process. The caller verifies process and window ownership.</summary>
internal sealed class GameCatalogIndex
{
    private readonly Dictionary<string, List<CatalogEntry>> _entries = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _blocked;

    public GameCatalogIndex(CatalogResult catalog, IEnumerable<string>? blockedExecutables = null)
    {
        _blocked = new((blockedExecutables ?? []).Select(CatalogPaths.LocalPath).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Entries)
        {
            if (CatalogPaths.IsRedistributable(entry))
            {
                _blocked.UnionWith(entry.Executables.Select(CatalogPaths.LocalPath).OfType<string>());
                continue;
            }
            foreach (var path in entry.Executables.Select(CatalogPaths.LocalPath).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!CatalogPaths.IsExecutable(path)) continue;
                if (!_entries.TryGetValue(path, out var matches)) _entries.Add(path, matches = []);
                matches.Add(entry);
            }
        }
    }

    public CatalogMatch Match(string? executable, bool isForeground)
    {
        if (executable != null && _entries.TryGetValue(executable, out var exact))
            return MatchKnown(executable, exact, isForeground);
        string? path = CatalogPaths.LocalPath(executable);
        if (path == null || !CatalogPaths.IsExecutable(path)) return new(CatalogMatchKind.Unknown);
        if (_entries.TryGetValue(path, out var candidates)) return MatchKnown(path, candidates, isForeground);
        return new(_blocked.Contains(path) ? CatalogMatchKind.Ignored : CatalogPaths.DefaultRole(path));
    }

    private CatalogMatch MatchKnown(string path, List<CatalogEntry> candidates, bool isForeground)
    {
        if (_blocked.Contains(path)) return new(CatalogMatchKind.Ignored);
        if (CatalogPaths.IsProtectedExecutable(path)) return new(CatalogMatchKind.Application);

        // An exact selection wins. For overlapping folder claims, prefer the
        // deepest verified install, while equal claims remain ambiguous.
        CatalogEntry? match = null;
        bool ambiguous = false;
        (int Evidence, int Specificity) precedence = (-1, 0);
        foreach (var candidate in candidates)
        {
            bool verified = !candidate.UnverifiedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase);
            int evidence = IsExplicitAttachment(candidate, path) ? 4 : candidate.Source == CatalogSource.Manual ? 3 : !verified ? 0 :
                HasGameEvidence(candidate, path) ? 2 : 1;
            int specificity = evidence != 4 && verified && candidate.Source == CatalogSource.Folder
                ? candidate.InstallDirectories.Where(root => CatalogPaths.IsWithin(path, root)).Select(root => root.Length).DefaultIfEmpty().Max()
                : 0;
            var rank = (evidence, specificity);
            if (rank.CompareTo(precedence) > 0)
            {
                match = candidate;
                precedence = rank;
                ambiguous = false;
            }
            else if (rank == precedence)
                ambiguous = true;
        }

        var entry = match!;
        if (candidates.FirstOrDefault(candidate => candidate.Rules.Inclusion == CatalogInclusion.Exclude) is { } excluded)
            return new(CatalogMatchKind.Ignored, excluded);
        bool attached = IsExplicitSelection(entry, path);
        bool tmodRuntime = IsVerifiedTModLoaderRuntime(entry, path);
        if (CatalogPaths.DefaultRole(path) is var role && role != CatalogMatchKind.Unknown && !attached && !tmodRuntime)
            return new(role, entry);
        foreach (var candidate in candidates)
        {
            if (candidate.Rules.Classification == AppClassification.Launcher ||
                candidate.Rules.LauncherExecutables.Any(raw => StringComparer.OrdinalIgnoreCase.Equals(CatalogPaths.LocalPath(raw), path)))
                return new(CatalogMatchKind.Launcher, entry);
            if (candidate.Rules.Classification == AppClassification.Application) return new(CatalogMatchKind.Application, entry);
            if (candidate.Rules.Classification == AppClassification.Companion) return new(CatalogMatchKind.Companion, entry);
        }
        // Only a verified Xbox discovery record naming this exact bundled runtime may
        // bypass the shared-Java guard. Ordinary Java remains ineligible.
        bool minecraftRuntime = entry.Source == CatalogSource.Xbox &&
            CatalogPaths.IsMinecraftBundledRuntime(path) &&
            entry.RendererExecutables is { } renderers && renderers.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            entry.InstallDirectories.Any(root => Path.GetFileName(root).Equals(CatalogPaths.MinecraftLauncherFolder, StringComparison.OrdinalIgnoreCase) &&
                StringComparer.OrdinalIgnoreCase.Equals(Path.Combine(root, CatalogPaths.MinecraftRuntimeRelative), path));
        if (CatalogPaths.IsJavaRuntime(path) && entry.Source != CatalogSource.Manual && !minecraftRuntime && !attached)
            return new(CatalogMatchKind.Application, entry);
        if (ambiguous) return new(CatalogMatchKind.Ambiguous);
        var rules = entry.Rules;
        if (!attached && entry.Source != CatalogSource.Manual && candidates.Any(candidate =>
            !candidate.UnverifiedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase) &&
            candidate.DetectedLaunchers.Contains(path, StringComparer.OrdinalIgnoreCase)))
            return new(CatalogMatchKind.Launcher, entry);
        if (precedence.Evidence == 0) return new(CatalogMatchKind.Inactive, entry);
        if (rules.Activation == CatalogActivation.ForegroundOnly && !isForeground) return new(CatalogMatchKind.Inactive, entry);
        return new(HasGameEvidence(entry, path) ? CatalogMatchKind.Game : CatalogMatchKind.Candidate, entry);
    }

    internal static bool IsExplicitAttachment(CatalogEntry entry, string path) =>
        !CatalogPaths.IsProtectedExecutable(path) &&
        entry.Rules.AttachedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase);

    internal static bool IsExplicitSelection(CatalogEntry entry, string path) =>
        IsExplicitAttachment(entry, path) ||
        !CatalogPaths.IsProtectedExecutable(path) && entry.Source == CatalogSource.Manual &&
            entry.Executables.Contains(path, StringComparer.OrdinalIgnoreCase);

    internal static bool IsVerifiedTModLoaderRuntime(CatalogEntry entry, string path) =>
        entry.Source == CatalogSource.Steam && entry.Id.Equals("steam:1281930", StringComparison.OrdinalIgnoreCase) &&
        entry.Executables.Contains(path, StringComparer.OrdinalIgnoreCase) &&
        entry.InstallDirectories.Any(install => CatalogPaths.IsTModLoaderRuntime(path, install)) &&
        !entry.UnverifiedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase);

    private static bool HasGameEvidence(CatalogEntry entry, string path) =>
        IsVerifiedTModLoaderRuntime(entry, path) ||
        IsExplicitAttachment(entry, path) ||
        entry.Rules.Classification == AppClassification.Game ||
        entry.Source == CatalogSource.Manual ||
        entry.Rules.ApprovedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            (entry.RendererExecutables is { } renderers
                ? renderers.Contains(path, StringComparer.OrdinalIgnoreCase)
                : LegacyRenderer(entry, path));

    private static bool LegacyRenderer(CatalogEntry entry, string path) =>
        // Older Epic snapshots contain only the validated manifest executable.
        entry.Source == CatalogSource.Epic ||
        entry.Source == CatalogSource.Folder && entry.InstallDirectories.Any(install =>
            CatalogPaths.IsWithin(path, install) && string.Equals(Path.GetFileNameWithoutExtension(path),
                Path.GetFileName(install), StringComparison.OrdinalIgnoreCase));
}
