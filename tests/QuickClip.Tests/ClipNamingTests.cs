using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class ClipNamingTests
{
    private static readonly DateTimeOffset Captured = new(2023, 8, 4, 6, 44, 24, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(ClipNaming.DefaultTemplate, null, "PlateUp__2023-08-04-06-44-24")]
    [InlineData(ClipNaming.DefaultTemplate, "最後の回", "PlateUp__2023-08-04-06-44-24 - 最後の回")]
    [InlineData(ClipNaming.DefaultTemplate, "  ", "PlateUp__2023-08-04-06-44-24")]
    [InlineData("{game}__{yyyy-MM-dd}__{HH-mm-ss}_{custom_title}", "Final", "PlateUp__2023-08-04__06-44-24_Final")]
    [InlineData("{game}_{custom_title}__{yyyy-MM-dd}-{HH-mm-ss}.mp4", "Final", "PlateUp_Final__2023-08-04-06-44-24")]
    [InlineData("{game}_{custom_title}__{yyyy-MM-dd}-{HH-mm-ss}.mp4", "", "PlateUp__2023-08-04-06-44-24")]
    public void Templates_use_the_original_offset_and_handle_missing_titles(string template, string? title, string expected) =>
        Assert.Equal(expected, ClipNaming.Stem(template, "PlateUp", Captured, title));

    [Fact]
    public void Existing_configured_templates_are_preserved_and_new_settings_use_the_new_default()
    {
        const string legacy = "{game}__{yyyy-MM-dd}__{HH-mm-ss}_{custom_title}";
        Assert.Equal(legacy, AppSettings.Parse("{\"ClipFilenameTemplate\":\"" + legacy + "\"}").ClipFilenameTemplate);
        Assert.Equal(ClipNaming.DefaultTemplate, AppSettings.Parse("{}").ClipFilenameTemplate);
    }

    [Theory]
    [InlineData("COM1.mp4", "_COM1.mp4")]
    [InlineData("LPT²", "_LPT²")]
    [InlineData("a/b:c*?", "a_b_c__")]
    [InlineData(" ... ", "Clip")]
    public void Sanitizes_windows_names(string name, string expected) => Assert.Equal(expected, ClipNaming.SafeComponent(name));

    [Fact]
    public void Limits_paths_and_preserves_unicode_pairs()
    {
        string dir = Path.Combine(Path.GetTempPath(), "clips");
        string path = ClipNaming.Candidate(dir, new string('a', 500) + "🦊", ".mp4", 2);
        Assert.EndsWith(" (2).mp4", path);
        Assert.True(path.Length <= 240);
        Assert.Equal("a", ClipNaming.SafeComponent("a🦊", 2));
        Assert.Throws<IOException>(() => ClipNaming.Candidate(Path.Combine(dir, new string('d', 240)), "clip", ".mp4"));
        Assert.Throws<ArgumentException>(() => ClipNaming.Stem("{wrong}", "game", Captured, ""));
    }

    [Fact]
    public void Publication_leaves_existing_files_intact()
    {
        using var fixture = new TempFiles();
        string existing = Path.Combine(fixture.Directory, "Clip.mp4");
        File.WriteAllText(existing, "existing");
        string staged = ClipNaming.ReserveStagingPath(fixture.Directory, ".mp4");
        File.WriteAllText(staged, "new");
        string result = ClipNaming.Publish(staged, fixture.Directory, "Clip", ".mp4");
        Assert.Equal("Clip (2).mp4", Path.GetFileName(result));
        Assert.Equal("existing", File.ReadAllText(existing));
        Assert.Equal("new", File.ReadAllText(result));
    }

    [Fact]
    public void Title_and_classification_are_local_overrides_and_do_not_follow_a_replaced_file()
    {
        if (!AppProfile.IsPersonal) return;
        using var fixture = new TempFiles();
        string path = Path.Combine(fixture.Directory, "existing.mp4");
        File.WriteAllText(path, "original");
        var details = new ClipDetails(Captured, "Corrected game", "Custom title");
        ClipDetailsStore.Write(path, details);
        Assert.Equal(details, ClipDetailsStore.Read(new FileInfo(path), "Folder", null));
        Assert.Equal("original", File.ReadAllText(path));
        File.AppendAllText(path, " changed");
        Assert.Equal("Folder", ClipDetailsStore.Read(new FileInfo(path), "Folder", null).Game);
    }

    private sealed class TempFiles : IDisposable
    {
        public string Directory { get; } = System.IO.Directory.CreateTempSubdirectory("qc-naming-").FullName;
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
