using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class SteamManifestParserTests
{
    [Fact]
    public void Reads_modern_and_legacy_library_suggestions_without_following_them()
    {
        var paths = SteamManifestParser.ParseLibraryFolders("""
            // Steam library roots
            "libraryfolders"
            {
                "0" { "path" "C:\\Steam Library" "apps" { "42" "123" } }
                "1" "D:\Steam"
                "2" "d:\\steam"
                "3" "\\\\server\\share"
                "4" "relative"
                "label" "E:\\not a library"
            }
            """);
        Assert.Equal(new[] { @"C:\Steam Library", @"D:\Steam" }, paths);
    }

    [Fact]
    public void Parses_escaped_quotes_comments_and_nested_fields()
    {
        var game = SteamManifestParser.ParseAppManifest("""
            "AppState" {
                "appid" "42"
                "name" "Example \"Deluxe\"" // comment
                "installdir" "Example"
                "MountedDepots" { "43" "1234" }
            }
            """);
        Assert.Equal(new SteamAppManifest(42, "Example \"Deluxe\"", "Example"), game);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AppState { appid 42 name Game installdir Game")]
    [InlineData("AppState { appid 42 appid 50 name Game installdir Game }")]
    [InlineData("AppState { appid 0 name Game installdir Game }")]
    [InlineData("AppState { appid 42 name Game installdir .. }")]
    [InlineData("AppState { appid 42 name Game installdir \"Game. \" }")]
    [InlineData("AppState { appid 42 name Game installdir \"C:\\Games\" }")]
    [InlineData("AppState { appid 42 name Game installdir CON.exe }")]
    public void Malformed_or_unsafe_app_manifests_are_rejected(string text) =>
        Assert.Null(SteamManifestParser.ParseAppManifest(text));

    [Fact]
    public void Excessively_nested_and_oversized_documents_are_rejected()
    {
        string nested = string.Concat(Enumerable.Repeat("child { ", 30)) + new string('}', 30);
        Assert.Null(SteamManifestParser.ParseAppManifest(nested));
        Assert.Null(SteamManifestParser.ParseAppManifest(new string(' ', SteamManifestParser.MaxDocumentSize + 1)));
    }
}
