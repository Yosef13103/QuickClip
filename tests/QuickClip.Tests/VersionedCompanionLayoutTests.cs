using QuickClip.Discovery;

namespace QuickClip.Tests;

public sealed class VersionedCompanionLayoutTests
{
    private const string Root = @"C:\Fixture\Discord";
    private static VersionedCompanionLayout Layout =>
        Assert.IsType<VersionedCompanionLayout>(VersionedCompanionLayout.Create(Root, "Discord.exe"));

    [Theory]
    [InlineData(@"C:\Fixture\Discord\app-1.0.9268\Discord.exe", "1.0.9268")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.9269\Discord.exe", "1.0.9269")]
    [InlineData(@"c:\fixture\discord\APP-1.2\discord.EXE", "1.2")]
    public void Old_and_new_observed_client_versions_match_same_layout(string path, string version)
    {
        Assert.Equal(Version.Parse(version), Layout.Match(path)?.Version);
    }

    [Theory]
    [InlineData(@"C:\Fixture\Discord\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\Update.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.9268\Update.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.9268\DiscordHookHelper.exe")]
    [InlineData(@"C:\Fixture\DiscordCanary\app-1.0.9268\Discord.exe")]
    [InlineData(@"C:\Other\Discord\app-1.0.9268\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.9268\nested\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\app-latest\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.2-preview\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.2\..\app-1.0.3\Discord.exe")]
    [InlineData(@"\\server\Discord\app-1.0.2\Discord.exe")]
    [InlineData(@"C:\Fixture\Discord\app-1.0.2\Discord.exe:stream")]
    public void Wrong_root_stub_helper_or_layout_cannot_match(string path) =>
        Assert.Null(Layout.Match(path));

    [Theory]
    [InlineData("Update.exe")]
    [InlineData("DiscordHookHelper64.exe")]
    [InlineData("Setup.exe")]
    [InlineData(@"..\Discord.exe")]
    [InlineData("Discord.dll")]
    [InlineData("QuickClip.exe")]
    public void Generic_or_unsafe_executable_cannot_define_layout(string executable) =>
        Assert.Null(VersionedCompanionLayout.Create(Root, executable));

    [Fact]
    public void Generic_versioned_app_is_not_discord_specific()
    {
        var layout = Assert.IsType<VersionedCompanionLayout>(
            VersionedCompanionLayout.Create(@"D:\Apps\ChatClient", "ChatClient.exe"));
        Assert.NotNull(layout.Match(@"D:\Apps\ChatClient\app-2.4.0\ChatClient.exe"));
        Assert.Null(layout.Match(@"D:\Apps\ChatClient\app-2.4.0\Other.exe"));
        Assert.Null(VersionedCompanionLayout.Create(@"D:\", "ChatClient.exe"));
    }
}
