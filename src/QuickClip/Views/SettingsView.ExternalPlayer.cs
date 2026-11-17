using System.Windows;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private void ExternalPlayer_Choose(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement anchor)
            ExternalPlayerDialogs.ChooseDefault(anchor,
                () => ExternalPlayerText.Text = ExternalPlayer.Label(AppSettings.Current.ExternalPlayerPath));
    }
}
