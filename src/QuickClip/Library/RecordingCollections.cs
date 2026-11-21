using System.Collections.Immutable;
using QuickClip.Discovery;

namespace QuickClip.Library;

// Explicit None/manual ID wins. Automatic mode uses only the already-verified
// renderer path; no discovery, capture permissions or process launch here.
internal sealed record GameCollectionPreference
{
    public bool Automatic { get; init; }
    public Guid? ManualCollection { get; init; }
    public ImmutableDictionary<string, Guid> Executables { get; init; } = ImmutableDictionary<string, Guid>.Empty;

    internal Guid? Resolve(string? executable)
    {
        if (!Automatic) return ManualCollection is Guid selected && selected != Guid.Empty ? selected : null;
        string? path = CatalogPaths.LocalPath(executable);
        if (path == null || Executables == null) return null;
        var matches = Executables.Where(pair => pair.Value != Guid.Empty &&
            StringComparer.OrdinalIgnoreCase.Equals(CatalogPaths.LocalPath(pair.Key), path))
            .Select(pair => pair.Value).Distinct().Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

internal static class RecordingCollections
{
    internal static Guid? Resolve(AppSettings settings, string? gameId, string? executable)
    {
        if (!settings.EnableLibraryIndex || !GameRecordingPolicy.IsId(gameId, 256) ||
            settings.RecordingCollections == null ||
            !settings.RecordingCollections.TryGetValue(gameId!, out var preference))
            return null;
        return preference?.Resolve(executable);
    }

    internal static bool SavePreference(string gameId, GameCollectionPreference preference)
    {
        if (!GameRecordingPolicy.IsId(gameId, 256)) throw new ArgumentException("Invalid game identity.");
        var settings = AppSettings.Current;
        var prior = settings.RecordingCollections ?? ImmutableDictionary<string, GameCollectionPreference>.Empty;
        settings.RecordingCollections = prior.SetItem(gameId, preference);
        if (settings.TrySave()) return true;
        settings.RecordingCollections = prior;
        return false;
    }
}
