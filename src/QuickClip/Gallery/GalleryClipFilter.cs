namespace QuickClip.Gallery;

internal sealed record GalleryTagFilter(long? Id, string Name)
{
    internal bool Matches(ClipDetails details) => Id is long id
        ? details.TagDefinitions.Any(t => t.Id == id)
        : details.Tags.Any(value => string.Equals(value.Normalize(), Name.Normalize(), StringComparison.OrdinalIgnoreCase));
}

internal sealed record GalleryClipFilter(string Search, string Tag, bool FavoritesOnly, bool FavoritesFirst)
{
    internal string[] ExactTags { get; init; } = [];
    internal GalleryTagFilter[] DefinitionFilters { get; init; } = [];
    internal bool MatchAllTags { get; init; } = true;
    public bool IsFiltered => Search.Length > 0 || Tag.Length > 0 || FavoritesOnly || ExactTags.Length > 0 || DefinitionFilters.Length > 0;

    internal bool MatchesTags(ClipDetails details)
    {
        if (ExactTags.Length == 0 && DefinitionFilters.Length == 0) return true;
        bool HasTag(string tag) => new GalleryTagFilter(null, tag).Matches(details);
        return MatchAllTags ? ExactTags.All(HasTag) && DefinitionFilters.All(t => t.Matches(details))
            : ExactTags.Any(HasTag) || DefinitionFilters.Any(t => t.Matches(details));
    }

    public IReadOnlyList<ClipItem> Apply(IEnumerable<ClipItem> clips, string folder, CancellationToken token = default)
    {
        var matches = new List<ClipItem>();
        foreach (var clip in clips)
        {
            token.ThrowIfCancellationRequested();
            var details = clip.Details;
            if (folder != ClipLibrary.AllClips && !string.Equals(details.Game, folder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!details.MatchesOrganizationFilter(Tag, FavoritesOnly) || !MatchesTags(details)) continue;
            if (Search.Length > 0 && !Contains(details.Title) && !Contains(details.Game) && !Contains(clip.File.Name) &&
                !Contains(details.Collection?.Name) && !details.Tags.Any(Contains))
                continue;
            matches.Add(clip);
        }
        return FavoritesFirst ? matches.OrderByDescending(clip => clip.IsFavorite).ToArray() : matches;
    }

    private bool Contains(string? value) => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true;
}
