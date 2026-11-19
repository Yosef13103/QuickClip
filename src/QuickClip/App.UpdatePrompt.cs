using System.Windows;
using System.Windows.Controls;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Updates;
using QuickClip.Views;

namespace QuickClip;

public partial class App
{
    private bool TryOfferPersonalUpdate(ReleaseInfo release)
    {
        if (_quitting || _quittingForUpdate || !AppProfile.IsPersonal || !Updater.IsEnabled ||
            Updater.IsChecking || Updater.Available != release || !Updater.CanDownloadPersonal(release) ||
            !AppSettings.Current.CheckForUpdates || Recorder.Instance.IsRunning ||
            UpdateShutdownBlock != null || BackgroundActivity.Snapshot(0).Length != 0 ||
            ModalDialog.IsOpen || _main is not { IsVisible: true, CanOfferUpdate: true, Content: Panel } window)
            return false;

        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = $"Installed: {AppVersion.Current.Display}  ·  Available: {release.Version.Display}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });
        content.Children.Add(new TextBlock { Text = "What's new", FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = release.Notes ?? UpdatePromptPolicy.Notes(null),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        bool skipped = false;
        var skip = new Button { Content = "Skip this update", Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0) };
        skip.Click += (_, _) => skipped = true;
        bool update = ModalDialog.Run(window, "QuickClip update available", content, "Update", "Cancel",
            safeDefault: true, dismissOnClick: skip, accent: true, wide: true, dismissInFooter: true);

        if (skipped)
        {
            var settings = AppSettings.Current;
            string? previous = settings.SkippedUpdateVersion;
            settings.SkippedUpdateVersion = release.Version.ToString();
            if (!settings.TrySave())
            {
                settings.SkippedUpdateVersion = previous;
                ModalDialog.Show(window, "Could not save the skipped version. This update is dismissed for this session only.",
                    "Update preference", MessageBoxButton.OK);
            }
        }
        else if (update && !_quitting && Updater.Available == release && !Recorder.Instance.IsRunning &&
            UpdateShutdownBlock == null && BackgroundActivity.Snapshot(0).Length == 0)
            _ = window.InstallOfferedUpdateAsync(release);
        return true;
    }
}
