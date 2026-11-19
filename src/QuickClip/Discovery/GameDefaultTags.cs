using System.Collections.Immutable;
using QuickClip.Gallery;

namespace QuickClip.Discovery;

internal static class GameDefaultTags
{
    internal static ImmutableArray<string> Parse(string text)
    {
        var tags = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim().Normalize()).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (tags.Length > ClipDetails.MaxTags || tags.Any(tag => !ClipDetails.IsValidTag(tag)))
            throw new ArgumentException($"Use up to {ClipDetails.MaxTags} tags, each up to {ClipDetails.MaxTagLength} characters.");
        return tags;
    }

    internal static string[] ForGame(string? gameId, bool manualDesktop,
        IEnumerable<CatalogEntry> catalog, GameProfileDocument document)
    {
        if (manualDesktop || string.IsNullOrEmpty(gameId)) return [];
        var entries = catalog.ToArray();
        var owners = entries.Where(entry => entry.Id == gameId || entry.AliasIds.Contains(gameId)).ToArray();
        if (owners.Length != 1) return [];
        var row = RecordingProfileSelection.Build(entries, document)
            .SingleOrDefault(choice => choice.GameId == owners[0].Id);
        if (row is not { Enabled: true, Policy.Tags: { } tags }) return [];
        // Legacy draft policies permitted larger tag arrays. Never silently
        // truncate them or lose an otherwise valid recording.
        if (tags.Length > ClipDetails.MaxTags || tags.Any(tag => !ClipDetails.IsValidTag(tag))) return [];
        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string[] ForSavedReplay(string? gameId, bool manualDesktop)
    {
        if (!AppProfile.IsPersonal || manualDesktop || string.IsNullOrEmpty(gameId)) return [];
        try
        {
            var loaded = GameProfileStore.ForCurrentProfile().Load();
            if (loaded.Status is not (GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Migrated)) return [];
            return ForGame(gameId, manualDesktop, GameCatalogRuntime.Rows(AppSettings.Current.Games), loaded.Document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Failure(DiagnosticCategory.Library, "Could not resolve default game tags", ex);
            return [];
        }
    }
}
