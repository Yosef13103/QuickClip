using System.Windows;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _refreshingStartup;

    private void RefreshStartupSettings(AppSettings settings)
    {
        _refreshingStartup = true;
        try
        {
            StartupCheck.IsEnabled = Startup.IsAvailable;
            StartupCheck.IsChecked = Startup.IsEnabled;
            StartupHint.Text = StartupCheck.IsEnabled
                ? "Uses the tray when the minimize-to-tray setting is enabled."
                : "Available in the installed edition of QuickClip Personal.";
            ReplayAtStartupCheck.IsChecked = settings.EnableReplayOnStartup ??
                (settings.RecordingEnabled && settings.PreferGameWindow);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            StartupCheck.IsEnabled = false;
            StartupHint.Text = "Windows startup settings are unavailable.";
        }
        finally { _refreshingStartup = false; }
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _refreshingStartup || !IsInitialized) return;
        try { Startup.Set(StartupCheck.IsChecked == true); }
        catch (Exception ex)
        {
            RefreshStartupSettings(AppSettings.Current);
            ModalDialog.Show(Window.GetWindow(this), ex.Message, "Windows startup could not be changed", image: MessageBoxImage.Error);
        }
    }

    private void ReplayStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _refreshingStartup || !IsInitialized || !AppProfile.IsPersonal) return;
        var settings = AppSettings.Current;
        bool? previous = settings.EnableReplayOnStartup;
        settings.EnableReplayOnStartup = ReplayAtStartupCheck.IsChecked == true;
        if (settings.TrySave()) return;
        settings.EnableReplayOnStartup = previous;
        RefreshStartupSettings(settings);
        ModalDialog.Show(Window.GetWindow(this), "QuickClip could not save the startup preference.",
            "Settings could not be saved", image: MessageBoxImage.Error);
    }
}
