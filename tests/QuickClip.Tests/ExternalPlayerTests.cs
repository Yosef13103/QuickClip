using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class ExternalPlayerTests
{
    [Fact]
    public void Preference_defaults_to_windows_and_round_trips_custom_path()
    {
        var settings = AppSettings.Parse("{}");
        Assert.Null(settings.ExternalPlayerPath);
        settings.ExternalPlayerPath = @"C:\Video Apps\player.exe";
        Assert.Equal(settings.ExternalPlayerPath, AppSettings.Parse(settings.ToJson()).ExternalPlayerPath);
    }

    [Fact]
    public void Custom_player_uses_exact_executable_and_one_media_argument_without_shell()
    {
        var temp = Directory.CreateTempSubdirectory("quickclip-player-");
        try
        {
            string video = Path.Combine(temp.FullName, "clip & test.mp4");
            string player = Path.Combine(temp.FullName, "custom player.exe");
            File.WriteAllText(video, "fixture");
            File.WriteAllText(player, "not executed");
            var system = ExternalPlayer.BuildStartInfo(video, null);
            Assert.True(system.UseShellExecute);
            Assert.Equal(video, system.FileName);
            var custom = ExternalPlayer.BuildStartInfo(video, player);
            Assert.False(custom.UseShellExecute);
            Assert.Equal(player, custom.FileName);
            Assert.Equal(video, Assert.Single(custom.ArgumentList));
            Assert.Throws<IOException>(() => ExternalPlayer.BuildStartInfo(video, "player.exe"));
            Assert.Throws<IOException>(() => ExternalPlayer.BuildStartInfo(video, video));
            Assert.Throws<IOException>(() => ExternalPlayer.BuildStartInfo(video, Path.Combine(temp.FullName, "missing.exe")));
            Assert.Throws<IOException>(() => ExternalPlayer.BuildStartInfo(Path.Combine(temp.FullName, "missing.mp4"), player));
        }
        finally { temp.Delete(recursive: true); }
    }
}
