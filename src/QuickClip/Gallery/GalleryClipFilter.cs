namespace QuickClip.Gallery;

internal sealed record GalleryClipFilter(string Search, string Tag, bool FavoritesOnly, bool FavoritesFirst)
{
    public bool IsFiltered => Search.Length > 0 || Tag.Length > 0 || FavoritesOnly;

    public IReadOnlyList<ClipItem> Apply(IEnumerable<ClipItem> clips, string folder, CancellationToken token = default)
    {
        var matches = new List<ClipItem>();
        foreach (var clip in clips)
        {
            token.ThrowIfCancellationRequested();
            var details = clip.Details;
            if (folder != ClipLibrary.AllClips && !string.Equals(details.Game, folder, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!details.MatchesOrganizationFilter(Tag, FavoritesOnly)) continue;
            if (Search.Length > 0 && !Contains(details.Title) && !Contains(details.Game) && !Contains(clip.File.Name))
                continue;
            matches.Add(clip);
        }
        return FavoritesFirst ? matches.OrderByDescending(clip => clip.IsFavorite).ToArray() : matches;
    }

    private bool Contains(string? value) => value?.Contains(Search, StringComparison.OrdinalIgnoreCase) == true;
}
