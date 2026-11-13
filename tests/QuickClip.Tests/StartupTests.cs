using Microsoft.Win32;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class StartupTests
{
    [Fact]
    public void Startup_entry_is_quoted_and_uses_only_the_personal_value()
    {
        // Use a disposable registry key outside the actual Windows Run and install locations.
        string path = @"Software\QuickClip.Tests." + Guid.NewGuid().ToString("N");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("QuickClip", "upstream");
            const string installed = @"C:\Applications\QuickClip Personal\QuickClip.exe";
            Startup.SetPersonalEntry(key, installed, true);
            Assert.Equal($"\"{installed}\" --windows-startup", key.GetValue(Startup.PersonalValueName));
            Startup.SetPersonalEntry(key, installed, false);
            Assert.Null(key.GetValue(Startup.PersonalValueName));
            Assert.Equal("upstream", key.GetValue("QuickClip"));
        }
        finally { Registry.CurrentUser.DeleteSubKey(path, throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Only_exact_installed_executable_qualifies_and_run_command_is_bounded()
    {
        Assert.True(Startup.MatchesInstallation(@"C:\App\QuickClip.exe", @"c:\app\quickclip.exe"));
        Assert.False(Startup.MatchesInstallation(@"C:\App\Old\QuickClip.exe", @"C:\App\QuickClip.exe"));
        Assert.False(Startup.MatchesInstallation("QuickClip.exe", "QuickClip.exe"));
        Assert.Null(Startup.PersonalCommand(@"C:\App\" + new string('a', 260) + ".exe"));
        Assert.Null(Startup.PersonalCommand("QuickClip.exe"));
    }

    [Fact]
    public void Existing_settings_and_onboarding_keep_their_startup_behavior()
    {
        if (!AppProfile.IsPersonal) return;
        var old = AppSettings.Parse("""{"RecordingEnabled":true,"PreferGameWindow":false}""");
        Assert.Null(old.EnableReplayOnStartup);
        old.ApplyReplayStartupPreference();
        Assert.True(old.RecordingEnabled);
        Assert.False(old.PreferGameWindow);

        var fresh = new AppSettings { EnableReplayOnStartup = true, PreferGameWindow = false };
        fresh.ApplyReplayStartupPreference();
        Assert.False(fresh.RecordingEnabled);
        fresh.ReplayOnboardingCompleted = true;
        fresh.ApplyReplayStartupPreference();
        Assert.True(fresh.RecordingEnabled);
        Assert.True(fresh.PreferGameWindow);
        fresh.EnableReplayOnStartup = false;
        fresh.ApplyReplayStartupPreference();
        Assert.False(fresh.RecordingEnabled);
        Assert.False(AppSettings.Parse(fresh.ToJson()).EnableReplayOnStartup);
    }
}
