using System.Collections.Immutable;
using QuickClip.Discovery;
using QuickClip.Library;
using QuickClip.Mpv;

namespace QuickClip.Tests;

public sealed class GameClipPathsAndTModLoaderTests
{
    [Fact]
    public void Relative_game_save_folders_are_nested_and_persist_without_changing_game_identity()
    {
        using var temp = new TemporaryDirectory();
        string root = Path.Combine(temp.Path, "Clips");
        Directory.CreateDirectory(root);
        var prefs = ImmutableDictionary<string, GameCollectionPreference>.Empty.Add("steam:1281930",
            new GameCollectionPreference { SaveSubfolder = @"Terraria\tModLoader" });
        var settings = new AppSettings { ClipsFolder = root, EnableLibraryIndex = true, RecordingCollections = prefs };
        string target = RecordingCollections.SaveDirectory(settings, "steam:1281930", "tModLoader");
        Assert.Equal(Path.Combine(root, "Terraria", "tModLoader"), target);
        Assert.True(Directory.Exists(target));
        settings.EnableLibraryIndex = false; // Physical routing stays independent of the Gallery index.
        Assert.Equal(target, RecordingCollections.SaveDirectory(settings, "steam:1281930", "tModLoader"));
        Assert.Equal(Path.Combine(root, "Terraria"), RecordingCollections.SaveDirectory(settings, "steam:105600", "Terraria"));
        Assert.Equal(@"Terraria\tModLoader", AppSettings.Parse(settings.ToJson()).RecordingCollections["steam:1281930"].SaveSubfolder);
        settings.RecordingCollections = settings.RecordingCollections.SetItem("steam:1281930",
            prefs["steam:1281930"] with { SaveSubfolder = @"Terraria\Modded\Calamity" });
        Assert.Equal(Path.Combine(root, "Terraria", "Modded", "Calamity"),
            RecordingCollections.SaveDirectory(settings, "steam:1281930", "tModLoader"));
        Assert.Equal("tModLoader", QuickClip.Gallery.ClipLibrary.InitialGame(root, Path.Combine(target, "test.mp4")));
    }

    [Fact]
    public void Nested_clips_remain_visible_to_gallery_depth_limited_scans()
    {
        using var temp = new TemporaryDirectory();
        string nested = Path.Combine(temp.Path, "Terraria", "tModLoader", "Calamity");
        Directory.CreateDirectory(nested);
        string clip = Path.Combine(nested, "boss.mp4");
        File.WriteAllText(clip, "fixture");
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 16 };
        Assert.Contains(clip, Directory.EnumerateFiles(temp.Path, "*.mp4", options));
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"Terraria\..\outside")]
    [InlineData(@"C:\outside")]
    [InlineData(@"a\b\c\d\e\f\g\h\i\j\k\l\m")]
    [InlineData(@"Terraria\SteelSeries Moments")]
    [InlineData(@"Terraria\QuickClip")]
    [InlineData(@"Terraria\CON")]
    public void Invalid_game_save_folders_are_rejected(string input) =>
        Assert.Throws<ArgumentException>(() => RecordingCollections.NormalizeSaveSubfolder(input));

    [Fact]
    public void Existing_linked_destination_is_rejected()
    {
        using var temp = new TemporaryDirectory();
        string root = Path.Combine(temp.Path, "Clips");
        Directory.CreateDirectory(root);
        string external = Path.Combine(temp.Path, "elsewhere");
        Directory.CreateDirectory(external);
        try { Directory.CreateSymbolicLink(Path.Combine(root, "Terraria"), external); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return; }
        var settings = new AppSettings
        {
            ClipsFolder = root, EnableLibraryIndex = true,
            RecordingCollections = ImmutableDictionary<string, GameCollectionPreference>.Empty.Add("steam:1281930",
                new GameCollectionPreference { SaveSubfolder = @"Terraria\tModLoader" })
        };
        Assert.Throws<IOException>(() => RecordingCollections.SaveDirectory(settings, "steam:1281930", "tModLoader"));
        Assert.False(Directory.Exists(Path.Combine(external, "tModLoader")));
    }

    [Fact]
    public async Task Steam_tmodloader_bundled_dotnet_is_game_but_other_dotnet_is_not()
    {
        using var temp = new TemporaryDirectory();
        string steam = Path.Combine(temp.Path, "Steam");
        string apps = Path.Combine(steam, "steamapps");
        string install = Path.Combine(apps, "common", "tModLoader");
        string runtime = Path.Combine(install, "dotnet", "dotnet.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
        File.WriteAllText(runtime, "fixture");
        File.WriteAllText(Path.Combine(apps, "appmanifest_1281930.acf"),
            """ "AppState" { "appid" "1281930" "name" "tModLoader" "installdir" "tModLoader" } """);
        var scan = await GameCatalogService.ScanAsync(new() { SteamLibraryRoots = [steam], DiscoverSteam = false });
        var index = new GameCatalogIndex(scan);
        var match = index.Match(runtime, true);
        Assert.Equal(CatalogMatchKind.Game, match.Kind);
        Assert.Equal("steam:1281930", match.Entry?.Id);
        Assert.Equal(CatalogMatchKind.Application, index.Match(Path.Combine(temp.Path, "other", "dotnet.exe"), true).Kind);
        Assert.Equal(CatalogMatchKind.Ignored, new GameCatalogIndex(scan, [runtime]).Match(runtime, true).Kind);
        Assert.True(CatalogPaths.IsTModLoaderRuntime(runtime, install));
        Assert.False(CatalogPaths.IsTModLoaderRuntime(Path.Combine(install, "other", "dotnet.exe"), install));
    }

    [Fact]
    public async Task Unrelated_steam_game_with_bundled_dotnet_does_not_gain_game_exception()
    {
        using var temp = new TemporaryDirectory();
        string steam = Path.Combine(temp.Path, "Steam");
        string apps = Path.Combine(steam, "steamapps");
        string runtime = Path.Combine(apps, "common", "OtherGame", "dotnet", "dotnet.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
        File.WriteAllText(runtime, "fixture");
        File.WriteAllText(Path.Combine(apps, "appmanifest_123456.acf"),
            """ "AppState" { "appid" "123456" "name" "OtherGame" "installdir" "OtherGame" } """);
        var scan = await GameCatalogService.ScanAsync(new() { SteamLibraryRoots = [steam], DiscoverSteam = false });
        Assert.NotEqual(CatalogMatchKind.Game, new GameCatalogIndex(scan).Match(runtime, true).Kind);
    }

    [Fact]
    public void Preview_zoom_is_clamped_and_linear_percentage_matches_mpv_log2_zoom()
    {
        Assert.Equal(.25, VideoPreviewZoom.Step(0, 120));
        Assert.Equal(-.25, VideoPreviewZoom.Step(0, -120));
        Assert.Equal(VideoPreviewZoom.Max, VideoPreviewZoom.Step(1.9, 120));
        Assert.Equal(VideoPreviewZoom.Min, VideoPreviewZoom.Step(-.9, -120));
        Assert.Equal(100, VideoPreviewZoom.Percent(0));
        Assert.Equal(200, VideoPreviewZoom.Percent(1));
        Assert.Equal(50, VideoPreviewZoom.Percent(-1));
    }

    [Fact]
    public void Cursor_anchored_zoom_keeps_point_stationary_until_image_edge()
    {
        const double viewportWidth = 1000, viewportHeight = 600, aspect = 16d / 9;
        var before = new VideoPreviewZoom.Transform(0, 0, 0);
        var after = VideoPreviewZoom.ZoomAt(before, 120, 650, 340,
            viewportWidth, viewportHeight, aspect);
        var oldImage = VideoPreviewZoom.ScaledSize(viewportWidth, viewportHeight, aspect, before.Zoom);
        var newImage = VideoPreviewZoom.ScaledSize(viewportWidth, viewportHeight, aspect, after.Zoom);
        double oldX = (650 - viewportWidth / 2) / oldImage.Width - before.PanX;
        double oldY = (340 - viewportHeight / 2) / oldImage.Height - before.PanY;
        double newX = (650 - viewportWidth / 2) / newImage.Width - after.PanX;
        double newY = (340 - viewportHeight / 2) / newImage.Height - after.PanY;
        Assert.InRange(Math.Abs(oldX - newX), 0, 1e-9);
        Assert.InRange(Math.Abs(oldY - newY), 0, 1e-9);
        Assert.True(after.PanX < 0);
        Assert.True(after.PanY < 0);
    }

    [Fact]
    public void Preview_drag_pans_scaled_picture_and_never_exposes_empty_space()
    {
        const double width = 1000, height = 600, aspect = 16d / 9;
        var enlarged = new VideoPreviewZoom.Transform(1, 0, 0);
        var dragged = VideoPreviewZoom.PanBy(enlarged, 100, -40, width, height, aspect);
        Assert.Equal(.05, dragged.PanX, precision: 6);
        Assert.InRange(dragged.PanY, -.04, -.035);
        var edge = VideoPreviewZoom.PanBy(dragged, 10000, 10000, width, height, aspect);
        Assert.InRange(edge.PanX, 0, .251);
        Assert.InRange(edge.PanY, 0, .234);
        var unzoomed = VideoPreviewZoom.PanBy(default, 500, 200, width, height, aspect);
        Assert.Equal(default, unzoomed);
        var reset = VideoPreviewZoom.ZoomAt(edge, -1200, 500, 300, width, height, aspect);
        Assert.Equal(VideoPreviewZoom.Min, reset.Zoom);
        Assert.Equal(0, reset.PanX);
        Assert.Equal(0, reset.PanY);
    }

    [Fact]
    public void Panning_respects_letterbox_and_invalid_video_dimensions()
    {
        var zoomed = new VideoPreviewZoom.Transform(1, 0, 0);
        var portrait = VideoPreviewZoom.PanBy(zoomed, 300, 250, 1200, 700, 9d / 16);
        Assert.Equal(0, portrait.PanX); // 2x portrait still fits horizontally.
        Assert.True(portrait.PanY > 0);
        Assert.Equal((0d, 0d), VideoPreviewZoom.ScaledSize(0, 700, 9d / 16, 1));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("qc-game-clips-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
