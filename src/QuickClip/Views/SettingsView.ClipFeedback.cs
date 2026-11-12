using System.Windows;
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
        ClipOverlayCheck.IsChecked = options.Enabled;
        ClipOverlayOptions.Visibility = options.Enabled ? Visibility.Visible : Visibility.Collapsed;
        var displays = new List<Choice> { new("Main display", "") };
        displays.AddRange(ClipNotificationOverlay.Displays().Select(d => new Choice(d.Label, d.Device)));
        if (!string.IsNullOrWhiteSpace(options.Display) && !displays.Any(d => Equals(d.Value, options.Display)))
            displays.Add(new("Saved display (disconnected)", options.Display));
        SetChoices(OverlayDisplayCombo, displays, options.Display ?? "");
        SetChoices(OverlayAnchorCombo, Enum.GetValues<OverlayAnchor>().Select(a => new Choice(AnchorLabels[(int)a], a)), options.Anchor);
        SetChoices(OverlayAnimationCombo, [new("Ripple", OverlayAnimation.Ripple), new("Fade", OverlayAnimation.Fade)], options.Animation);
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
        settings.ClipWindowsNotification = WindowsClipNotificationCheck.IsChecked == true;
        if (AppProfile.IsPersonal)
            ClipOverlayOptions.Visibility = ClipOverlayCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (AppProfile.IsPersonal && OverlayAnchorCombo.SelectedItem is Choice { Value: OverlayAnchor anchor } &&
            OverlaySizeCombo.SelectedItem is Choice { Value: double size } &&
            OverlayOpacityCombo.SelectedItem is Choice { Value: double opacity } &&
            OverlayDurationCombo.SelectedItem is Choice { Value: double duration } &&
            OverlayAnimationCombo.SelectedItem is Choice { Value: OverlayAnimation animation })
        {
            settings.ClipOverlay = new ClipOverlayOptions
            {
                Enabled = ClipOverlayCheck.IsChecked == true,
                Display = (OverlayDisplayCombo.SelectedItem as Choice)?.Value as string,
                Anchor = anchor, Size = size, Opacity = opacity, Seconds = duration, Animation = animation,
            };
        }
        settings.Save();
    }

    private void TestClipOverlay_Click(object sender, RoutedEventArgs e) => App.Host?.TestClipOverlay();
}
