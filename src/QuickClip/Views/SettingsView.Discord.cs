using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickClip.Recording;
using QuickClip.Recording.Companion;

namespace QuickClip.Views;

public partial class SettingsView
{
    private DispatcherTimer? _discordUiTimer;
    private bool _discordLoading;


    private void InitializeDiscord()
    {
        if (!AppProfile.IsPersonal) return;
        _discordLoading = true;
        try
        {
            var s = AppSettings.Current;
            DiscordEnableCheck.IsChecked = s.DiscordCaptureEnabled;
            DiscordModeCombo.SelectedItem = DiscordModeCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag?.ToString(), s.DiscordCaptureMode.ToString()))
                ?? DiscordModeCombo.Items[0];
        }
        finally { _discordLoading = false; }
        _discordUiTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _discordUiTimer.Tick += (_, _) => RefreshDiscordUi();
        Loaded += (_, _) => RefreshDiscordUi();
        Unloaded += (_, _) => _discordUiTimer.Stop();
        IsVisibleChanged += (_, _) => RefreshDiscordUi();
        RefreshDiscordUi();
    }

    private void RefreshDiscordUi()
    {
        if (!AppProfile.IsPersonal || _discordUiTimer == null) return;
        bool show = IsVisible && CompanionsSection.IsChecked == true;
        if (!show) { _discordUiTimer.Stop(); return; }
        _discordUiTimer.Start();
        var s = AppSettings.Current;
        DiscordStatusText.Text = Recorder.Instance.DiscordStatus;
        bool manual = s.DiscordCaptureMode == DiscordCaptureMode.Manual;
        DiscordManualButton.Visibility = manual && s.DiscordCaptureEnabled ? Visibility.Visible : Visibility.Collapsed;
        DiscordManualButton.Content = Recorder.Instance.ManualDiscordRequested ? "Stop Discord capture" : "Start Discord capture";
        DiscordManualButton.IsEnabled = s.RecordingEnabled;
        DiscordModeCombo.IsEnabled = s.DiscordCaptureEnabled;
    }

    private async void DiscordOption_Click(object sender, RoutedEventArgs e) =>
        await SaveDiscordOptionsAsync();

    private async void DiscordMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _discordLoading) return;
        await SaveDiscordOptionsAsync();
    }

    private async Task SaveDiscordOptionsAsync()
    {
        if (_discordLoading || !AppProfile.IsPersonal) return;
        if (DiscordModeCombo.SelectedItem is not ComboBoxItem option ||
            !Enum.TryParse<DiscordCaptureMode>(option.Tag?.ToString(), out var mode)) return;
        var s = AppSettings.Current;
        bool previousEnabled = s.DiscordCaptureEnabled;
        var previousMode = s.DiscordCaptureMode;
        bool nextEnabled = DiscordEnableCheck.IsChecked == true;
        if (nextEnabled == previousEnabled && mode == previousMode) return;
        s.DiscordCaptureEnabled = nextEnabled;
        s.DiscordCaptureMode = mode;
        if (!s.TrySave())
        {
            s.DiscordCaptureEnabled = previousEnabled;
            s.DiscordCaptureMode = previousMode;
            _discordLoading = true;
            try
            {
                DiscordEnableCheck.IsChecked = previousEnabled;
                DiscordModeCombo.SelectedItem = DiscordModeCombo.Items.Cast<ComboBoxItem>()
                    .First(item => Equals(item.Tag?.ToString(), previousMode.ToString()));
            }
            finally { _discordLoading = false; }
            ModalDialog.Show(Window.GetWindow(this), "Could not save Discord recording settings.", "Companions");
            return;
        }
        await Recorder.Instance.ApplySettingsAsync();
        RefreshDiscordUi();
    }

    private async void DiscordManual_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal || !AppSettings.Current.DiscordCaptureEnabled ||
            AppSettings.Current.DiscordCaptureMode != DiscordCaptureMode.Manual) return;
        DiscordManualButton.IsEnabled = false;
        try
        {
            await Recorder.Instance.SetManualDiscordAsync(!Recorder.Instance.ManualDiscordRequested);
        }
        finally { RefreshDiscordUi(); }
    }
}
