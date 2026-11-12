using QuickClip.Shell;
using QuickClip.Updates;

namespace QuickClip.Tests;

public sealed class ProfileTests
{
    [Fact]
    public void Tests_use_separate_settings_logs_and_clip_directories()
    {
        string root = Path.GetDirectoryName(AppProfile.SettingsDirectory)!;
        Assert.StartsWith("quickclip-test-profile-", Path.GetFileName(root));
        Assert.Equal(Path.Combine(root, "settings", "settings.json"), AppSettings.FilePath);
        Assert.Equal(Path.Combine(root, "local", "quickclip.log"), Log.FilePath);
        Assert.Equal(Path.Combine(root, "clips"), AppSettings.Current.ClipsFolder);
        Assert.Equal(Path.Combine(root, "shared"), ClipSharing.Folder);
        Assert.Equal(Path.Combine(root, "temp"), AppProfile.TempDirectory);
        Assert.False(AppSettings.Current.LoadedFromFile);
    }

    [Fact]
    public async Task Personal_build_has_separate_ipc_and_disables_side_effects_by_default()
    {
        var settings = AppSettings.Parse("{}");
        if (!AppProfile.IsPersonal)
        {
            Assert.Equal(@"Local\QuickClip.Instance", AppProfile.MutexName);
            Assert.True(settings.RecordingEnabled);
            Assert.True(settings.CheckForUpdates);
            Assert.True(Updater.IsEnabled);
            return;
        }

        Assert.Equal("QuickClip Personal", AppProfile.Name);
        Assert.Equal(@"Local\QuickClip.Personal.Instance", AppProfile.MutexName);
        Assert.StartsWith("QuickClip.Personal.Instance.", AppProfile.PipeName);
        Assert.False(settings.RecordingEnabled);
        Assert.False(settings.RecordMic);
        Assert.Equal(0, settings.HotkeyKey); // parsed existing Personal profile keeps the old unbound default
        Assert.Equal(0x79, new AppSettings().HotkeyKey);
        Assert.False(settings.CheckForUpdates);
        Assert.False(Startup.IsEnabled);
        Assert.False(ShellIntegration.IsRegistered());
        Assert.Throws<InvalidOperationException>(() => Startup.Set(true));
        Assert.Throws<InvalidOperationException>(ShellIntegration.Register);
        Assert.Throws<InvalidOperationException>(ShellIntegration.Unregister);

        var release = new ReleaseInfo(AppVersion.Current, "test", "Test", "",
            new ReleaseAsset("", 0, null), new ReleaseAsset("", 0, null));
        Assert.False(Updater.CanInstall(release));
        Assert.Null(await Updater.CheckAsync());
        Assert.Null(Updater.LastChecked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Updater.GetReleasesJsonAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Updater.PrepareInstallAsync(release, false, new Progress<double>(), CancellationToken.None));
    }
}
