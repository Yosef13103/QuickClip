using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class GalleryDiskFolderTests
{
    private const string Root = @"C:\Clips";

    [Fact]
    public void Same_named_children_in_different_games_keep_distinct_path_identity()
    {
        var a = GalleryDiskFolders.Locate(
            @"C:\Clips\DARK SOULS™ III\Convergence\one.mp4", [Root]);
        var b = GalleryDiskFolders.Locate(
            @"C:\Clips\Elden Ring\Convergence\two.mp4", [Root]);
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal("DARK SOULS™ III", a.Game);
        Assert.Equal("Elden Ring", b.Game);
        var first = Assert.Single(a.ChildAncestors());
        var second = Assert.Single(b.ChildAncestors());
        Assert.Equal("Convergence", first.Name);
        Assert.Equal("Convergence", second.Name);
        Assert.NotEqual(first.Path, second.Path);
        Assert.Null(first.Parent);
        Assert.Equal(1, first.Depth);
        Assert.Equal("DARK SOULS™ III", GalleryDiskFolders.PresentedGame(
            new ClipDetails(DateTimeOffset.Now, "Convergence"), a));
        Assert.Equal("Elden Ring", GalleryDiskFolders.PresentedGame(
            new ClipDetails(DateTimeOffset.Now, "Convergence"), b));
    }

    [Fact]
    public void Legacy_Discord_and_verified_Discord_use_same_display_group_without_modifying_metadata()
    {
        var legacy = new ClipDetails(DateTimeOffset.Now, "Movies & Shows");
        var location = GalleryDiskFolders.Locate(@"C:\Clips\Discord\Movies & Shows\a.mp4", [Root]);
        Assert.NotNull(location);
        var presented = GalleryDiskFolders.PresentedGame(legacy, location);
        Assert.Equal("Discord", presented);
        const string id = "discord-verified";
        var verified = new ClipDetails(DateTimeOffset.Now, "Discord") { GameId = id };
        Assert.Equal(GalleryDiskFolders.PresentedKey(verified, "Discord", id),
            GalleryDiskFolders.PresentedKey(legacy, presented, id));
        Assert.Null(legacy.GameId);
        Assert.Equal("Movies & Shows", legacy.Game);
        Assert.NotEqual(GalleryDiskFolders.PresentedKey(legacy, presented, null),
            GalleryDiskFolders.PresentedKey(legacy, presented, id));
    }

    [Fact]
    public void Scoped_subfolders_are_root_aware_and_support_multiple_levels()
    {
        var roots = new[] { Root, @"D:\ImportedClips" };
        var location = GalleryDiskFolders.Locate(
            @"D:\ImportedClips\Elden Ring\Convergence\Chapter One\clip.mp4", roots);
        Assert.NotNull(location);
        Assert.Equal(@"D:\ImportedClips", location.Root);
        var ancestors = location.ChildAncestors().ToArray();
        Assert.Equal(2, ancestors.Length);
        Assert.Equal("Convergence", ancestors[0].Name);
        Assert.Equal("Chapter One", ancestors[1].Name);
        Assert.Equal(ancestors[0].Path, ancestors[1].Parent);
        Assert.Equal(2, ancestors[1].Depth);
        Assert.NotEqual(GalleryDiskFolders.Locate(
            @"C:\Clips\Elden Ring\Convergence\clip.mp4", roots)!.ChildAncestors().First().Path,
            ancestors[0].Path);
    }

    [Fact]
    public void Physical_child_selection_never_matches_another_game_or_root()
    {
        var details = new ClipDetails(DateTimeOffset.Now, "Convergence");
        var game = new GalleryFolderKey(null, "ELDEN RING");
        var child = new GalleryFolderSelection(game, true, PhysicalDirectory: @"C:\Clips\Elden Ring\Convergence");
        Assert.True(child.Matches(details, @"C:\Clips\Elden Ring\Convergence\clip.mp4", game));
        Assert.True(child.Matches(details, @"C:\Clips\Elden Ring\Convergence\Chapter 1\clip.mp4", game));
        Assert.False(child.Matches(details, @"C:\Clips\DARK SOULS™ III\Convergence\clip.mp4", game));
        Assert.False(child.Matches(details, @"D:\ImportedClips\Elden Ring\Convergence\clip.mp4", game));
        Assert.False(child.Matches(details, @"C:\Clips\Elden Ring\Convergence 2\clip.mp4", game));
        Assert.False(child.Matches(details, @"C:\Clips\Elden Ring\Convergence\clip.mp4",
            new GalleryFolderKey(null, "DARK SOULS™ III")));
    }

    [Fact]
    public void Explicit_or_verified_game_labels_are_not_replaced_by_folder_names()
    {
        var location = GalleryDiskFolders.Locate(
            @"C:\Clips\Elden Ring\Convergence\clip.mp4", [Root]);
        Assert.NotNull(location);
        Assert.Equal("Modded Elden Ring", GalleryDiskFolders.PresentedGame(
            new ClipDetails(DateTimeOffset.Now, "Modded Elden Ring"), location));
        Assert.Equal("Convergence", GalleryDiskFolders.PresentedGame(
            new ClipDetails(DateTimeOffset.Now, "Convergence") { GameId = "game-123" }, location));
        Assert.Null(GalleryDiskFolders.Locate(@"E:\Outside\clip.mp4", [Root]));
    }
}
