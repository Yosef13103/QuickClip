using System.Collections.Immutable;
using QuickClip.Discovery;
using QuickClip.Gallery;

namespace QuickClip.Library;

// Explicit None/manual ID wins. Automatic mode uses only the already-verified
// renderer path; no discovery, capture permissions or process launch here.
internal sealed record GameCollectionPreference
{
    public bool Automatic { get; init; }
    public Guid? ManualCollection { get; init; }
    // Optional relative physical folder for new clips; independent from Gallery collection metadata.
    public string? SaveSubfolder { get; init; }
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
    internal static string? NormalizeSaveSubfolder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = LibraryFiles.RelativePath(value.Trim());
        string[] segments = normalized.Split('\\');
        if (normalized.Length > 240 || segments.Length > 12 || ClipLibrary.IsExcluded(normalized, null) ||
            segments.Any(segment => segment.Equals("QuickClip", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(ProjectLibrary.FolderName, StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Cache", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("thumbs", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("$Recycle.Bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use a relative path of at most 12 safe folders inside Clips (240 characters maximum), outside excluded folders.");
        return normalized;
    }

    internal static string SaveDirectory(AppSettings settings, string? gameId, string defaultFolder)
    {
        string relative = defaultFolder;
        if (GameRecordingPolicy.IsId(gameId, 256) &&
            settings.RecordingCollections?.TryGetValue(gameId!, out var preference) == true)
        {
            try { relative = NormalizeSaveSubfolder(preference?.SaveSubfolder) ?? defaultFolder; }
            catch (ArgumentException) { /* Legacy or corrupted settings cannot redirect writes. */ }
        }
        string root = Path.GetFullPath(settings.ClipsFolder);
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!CatalogPaths.IsWithin(result, root) || ClipLibrary.IsMomentsPath(result) ||
            ClipLibrary.IsExcluded(relative, settings.LibraryExclusions))
            throw new IOException("The clip destination is excluded or outside the approved Clips folder.");
        // Reject existing reparse components, including junctions, before writing through them.
        string current = root;
        var rootState = LibraryFiles.InspectPath(current, directory: true);
        if (rootState == ClipAvailability.Unavailable) throw new IOException("The Clips folder is unavailable.");
        if (rootState == ClipAvailability.Missing) Directory.CreateDirectory(current);
        if (LibraryFiles.InspectPath(current, directory: true) != ClipAvailability.Available)
            throw new IOException("The Clips folder is unavailable.");
        foreach (var part in Path.GetRelativePath(root, result).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            var state = LibraryFiles.InspectPath(current, directory: true);
            if (state == ClipAvailability.Unavailable) throw new IOException("Clip destination includes an unavailable or linked folder.");
            if (state == ClipAvailability.Missing) Directory.CreateDirectory(current);
        }
        return result;
    }
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
        preference = preference with { SaveSubfolder = NormalizeSaveSubfolder(preference.SaveSubfolder) };
        var settings = AppSettings.Current;
        var prior = settings.RecordingCollections ?? ImmutableDictionary<string, GameCollectionPreference>.Empty;
        settings.RecordingCollections = prior.SetItem(gameId, preference);
        if (settings.TrySave()) return true;
        settings.RecordingCollections = prior;
        return false;
    }
}
