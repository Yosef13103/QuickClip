using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private static readonly string[] AnchorLabels =
        ["Top-left", "Top-center", "Top-right", "Middle-left", "Center", "Middle-right", "Bottom-left", "Bottom-center", "Bottom-right"];

    private void LoadClipFeedback(AppSettings settings)
    {
        WindowsClipNotificationCheck.IsChecked = settings.ClipWindowsNotification;
        ClipOverlayPanel.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        if (!AppProfile.IsPersonal) return;
        var options = (settings.ClipOverlay ?? new()).Normalize();
        WindowsClipNotificationCheck.Visibility = Visibility.Collapsed;
        ClipNotificationsCheck.IsChecked = settings.ClipNotificationsEnabled && (settings.ClipWindowsNotification || options.Enabled);
        SetChoices(ClipNotificationModeCombo,
            [new("Windows notification", ClipNotificationMode.Windows), new("QuickClip overlay", ClipNotificationMode.Overlay),
             new("Windows and QuickClip overlay", ClipNotificationMode.Both)],
            options.Enabled ? settings.ClipWindowsNotification ? ClipNotificationMode.Both : ClipNotificationMode.Overlay : ClipNotificationMode.Windows);
        OverlayShowTitle.IsChecked = options.ShowTitle;
        OverlayShowGame.IsChecked = options.ShowGame;
        OverlayShowSource.IsChecked = options.ShowSource;
        OverlayShowDuration.IsChecked = options.ShowDuration;
        ShowOverlayImage(options.ImagePath);
        UpdateFeedbackVisibility();
        var displays = new List<Choice> { new("Main display", "") };
        displays.AddRange(ClipNotificationOverlay.Displays().Select(d => new Choice(d.Label, d.Device)));
        if (!string.IsNullOrWhiteSpace(options.Display) && !displays.Any(d => Equals(d.Value, options.Display)))
            displays.Add(new("Saved display (disconnected)", options.Display));
        SetChoices(OverlayDisplayCombo, displays, options.Display ?? "");
        SetChoices(OverlayAnchorCombo, Enum.GetValues<OverlayAnchor>().Select(a => new Choice(AnchorLabels[(int)a], a)), options.Anchor);
        SetChoices(OverlayAnimationCombo, [new("Slide in", OverlayAnimation.Slide), new("Fade", OverlayAnimation.Fade),
            new("Ripple", OverlayAnimation.Ripple), new("No animation", OverlayAnimation.None)], options.Animation);
        SetNumbers(OverlaySizeCombo, [48, 60, 72, 88, 104], options.Size, value => $"{value:0} DIP");
        SetNumbers(OverlayOpacityCombo, [.3, .5, .7, .9, 1], options.Opacity, value => $"{value:P0}");
        SetNumbers(OverlayDurationCombo, [.8, 1.4, 2, 3, 5], options.Seconds, value => $"{value:0.#} s");
    }

    private void SetChoices(ComboBox combo, IEnumerable<Choice> choices, object selected)
    {
        combo.ItemsSource = choices.ToArray();
        Select(combo, selected);
    }

    private void SetNumbers(ComboBox combo, double[] choices, double selected, Func<double, string> label) =>
        SetChoices(combo, choices.Append(selected).Distinct().Order().Select(n => new Choice(label(n), n)), selected);

    private void ClipFeedback_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var settings = AppSettings.Current;
        if (!AppProfile.IsPersonal)
        {
            settings.ClipWindowsNotification = WindowsClipNotificationCheck.IsChecked == true;
            settings.Save();
            return;
        }

        if (ClipNotificationModeCombo.SelectedItem is not Choice { Value: ClipNotificationMode mode }) return;
        settings.ClipNotificationsEnabled = ClipNotificationsCheck.IsChecked == true;
        settings.ClipWindowsNotification = mode is ClipNotificationMode.Windows or ClipNotificationMode.Both;
        UpdateFeedbackVisibility();
        if (OverlayAnchorCombo.SelectedItem is Choice { Value: OverlayAnchor anchor } &&
            OverlaySizeCombo.SelectedItem is Choice { Value: double size } &&
            OverlayOpacityCombo.SelectedItem is Choice { Value: double opacity } &&
            OverlayDurationCombo.SelectedItem is Choice { Value: double duration } &&
            OverlayAnimationCombo.SelectedItem is Choice { Value: OverlayAnimation animation })
        {
            settings.ClipOverlay = (settings.ClipOverlay ?? new()) with
            {
                Enabled = mode is ClipNotificationMode.Overlay or ClipNotificationMode.Both,
                ShowTitle = OverlayShowTitle.IsChecked == true,
                ShowGame = OverlayShowGame.IsChecked == true,
                ShowSource = OverlayShowSource.IsChecked == true,
                ShowDuration = OverlayShowDuration.IsChecked == true,
                Display = (OverlayDisplayCombo.SelectedItem as Choice)?.Value as string,
                Anchor = anchor, Size = size, Opacity = opacity, Seconds = duration, Animation = animation,
            };
        }
        settings.Save();
    }

    private void UpdateFeedbackVisibility()
    {
        bool enabled = ClipNotificationsCheck.IsChecked == true;
        ClipNotificationModeCombo.IsEnabled = enabled;
        ClipOverlayOptions.Visibility = enabled && ClipNotificationModeCombo.SelectedItem is Choice
            { Value: ClipNotificationMode.Overlay or ClipNotificationMode.Both } ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChooseOverlayImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an overlay image", Filter = "PNG or JPEG images|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        ClearOverlayImage.IsEnabled = false;
        try
        {
            if (await Task.Run(() => ClipOverlayImage.Load(dialog.FileName)) == null)
            {
                ShowOverlayImage(AppSettings.Current.ClipOverlay?.ImagePath);
                OverlayImageStatus.Text = "Choose a valid PNG or JPEG up to 4 MB and 2048 × 2048.";
                return;
            }
            SaveOverlayImage(dialog.FileName);
        }
        finally { button.IsEnabled = true; }
    }

    private void ClearOverlayImage_Click(object sender, RoutedEventArgs e) => SaveOverlayImage(null);

    private void SaveOverlayImage(string? path)
    {
        var settings = AppSettings.Current;
        settings.ClipOverlay = (settings.ClipOverlay ?? new()) with { ImagePath = path };
        settings.Save();
        ShowOverlayImage(path);
    }

    private void ShowOverlayImage(string? path)
    {
        ClearOverlayImage.IsEnabled = path != null;
        OverlayImageStatus.Text = path == null ? "" : Path.GetFileName(path);
    }

    private void TestClipOverlay_Click(object sender, RoutedEventArgs e) => App.Host?.TestClipOverlay();
}
