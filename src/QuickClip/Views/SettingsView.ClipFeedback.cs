using System.Globalization;
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

    private bool _loadingSoundOutputs;
    private System.Windows.Threading.DispatcherTimer? _soundVolumeSave;

    private void ShowSelectedClipSound(string? path)
    {
        SelectedClipSound.Text = string.IsNullOrWhiteSpace(path) ? "Default camera-shutter sound" : Path.GetFileName(path);
        ResetClipSoundButton.IsEnabled = !string.IsNullOrWhiteSpace(path);
        bool missing = !string.IsNullOrWhiteSpace(path) && !File.Exists(path);
        ClipSoundFileError.Text = missing ? "The selected sound file is missing. Choose a new sound or restore the default." : "";
        ClipSoundFileError.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChooseClipSound_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a custom clip-save sound",
            Filter = "Audio|*.wav;*.mp3;*.flac;*.ogg;*.opus;*.m4a;*.aac;*.aiff;*.aif",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        ChooseClipSoundButton.IsEnabled = false;
        try
        {
            byte[]? sound = await ClipSoundCustom.LoadAsync(dialog.FileName);
            if (sound == null)
            {
                ClipSoundFileError.Text = "Could not load that sound. Choose a valid local audio file up to 16 MiB; the first 3 seconds are used.";
                ClipSoundFileError.Visibility = Visibility.Visible;
                return;
            }
            AppSettings.Current.ClipSoundFilePath = dialog.FileName;
            AppSettings.Current.Save();
            ShowSelectedClipSound(dialog.FileName);
            SoundOutputStatus.Text = SoundOverlapMessage();
        }
        finally { ChooseClipSoundButton.IsEnabled = true; }
    }

    private void ResetClipSound_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.ClipSoundFilePath = null;
        AppSettings.Current.Save();
        ShowSelectedClipSound(null);
    }

    private void SoundOutputOpened(object sender, EventArgs e) => LoadSoundOutputs();

    private void LoadSoundOutputs()
    {
        var choices = new List<Choice> { new("System Default", "") };
        try { choices.AddRange(ClipSoundOutput.Devices().Select(device => new Choice(device.Name, device.Id))); }
        catch (System.Runtime.InteropServices.COMException) { }
        string selected = AppSettings.Current.ClipSoundOutputId ?? "";
        bool missing = !choices.Any(choice => string.Equals((string)choice.Value, selected, StringComparison.OrdinalIgnoreCase));
        if (missing) choices.Add(new("Saved output (unavailable)", selected));
        _loadingSoundOutputs = true;
        try
        {
            SoundOutputCombo.ItemsSource = choices;
            SoundOutputCombo.SelectedItem = choices.First(choice =>
                string.Equals((string)choice.Value, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _loadingSoundOutputs = false; }
        SoundOutputStatus.Text = missing ? "Reconnect the saved output or choose another device." : SoundOverlapMessage();
    }

    private void SoundVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsInitialized || !AppProfile.IsPersonal) return;
        AppSettings.Current.ClipSoundVolume = (int)e.NewValue;
        _soundVolumeSave ??= new(TimeSpan.FromMilliseconds(300), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) => { _soundVolumeSave!.Stop(); AppSettings.Current.Save(); }, Dispatcher);
        _soundVolumeSave.Stop();
        _soundVolumeSave.Start();
    }

    private void SoundOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _loadingSoundOutputs || SoundOutputCombo.SelectedItem is not Choice { Value: string id }) return;
        AppSettings.Current.ClipSoundOutputId = id.Length == 0 ? null : id;
        AppSettings.Current.Save();
        SoundOutputStatus.Text = SoundOverlapMessage();
    }

    private static string SoundOverlapMessage()
    {
        var settings = AppSettings.Current;
        return settings.ClipSoundOutputId is { Length: > 0 } id && settings.UseAudioEndpoints &&
            (settings.EndpointTracks ?? []).Any(track => track.Enabled && track.Flow == Recording.EndpointFlow.Playback &&
                string.Equals(track.DeviceId, id, StringComparison.OrdinalIgnoreCase))
            ? "A recording track also captures this output. The chime may be audible in a later clip." : "";
    }

    private void LoadClipFeedback(AppSettings settings)
    {
        WindowsClipNotificationCheck.IsChecked = settings.ClipWindowsNotification;
        SoundOutputPanel.Visibility = ClipOverlayPanel.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        if (!AppProfile.IsPersonal) return;
        LoadSoundOutputs();
        ShowSelectedClipSound(settings.ClipSoundFilePath);
        SoundVolumeSlider.Value = Math.Clamp(settings.ClipSoundVolume, 0, 100);
        var options = (settings.ClipOverlay ?? new()).Normalize();
        WindowsClipNotificationCheck.Visibility = Visibility.Collapsed;
        ClipNotificationsCheck.IsChecked = settings.ClipNotificationsEnabled && (settings.ClipWindowsNotification || options.Enabled);
        SetChoices(ClipNotificationModeCombo,
            [new("Windows notification", ClipNotificationMode.Windows), new("QuickClip overlay", ClipNotificationMode.Overlay),
             new("Windows and QuickClip overlay", ClipNotificationMode.Both)],
            options.Enabled ? settings.ClipWindowsNotification ? ClipNotificationMode.Both : ClipNotificationMode.Overlay : ClipNotificationMode.Windows);
        var events = settings.ClipEvents ?? new();
        ReplayEnabledNotice.IsChecked = events.ReplayEnabled;
        ClipSavedNotice.IsChecked = events.Saved;
        DesktopStartedNotice.IsChecked = events.DesktopStarted;
        DesktopStoppedNotice.IsChecked = events.DesktopStopped;
        SaveFailedNotice.IsChecked = events.Failed;
        IdleSaveNotificationCheck.IsChecked = settings.ShowInactiveSaveNotifications;
        SetChoices(OverlayStyleCombo, [new("Compact banner", OverlayStyle.Compact),
            new("Detailed banner", OverlayStyle.Detailed), new("Image only (transparent)", OverlayStyle.ImageOnly)],
            options.ImageOnly ? OverlayStyle.ImageOnly : options.Style);
        OverlayShowResolution.IsChecked = options.ShowResolution;
        OverlayShowFps.IsChecked = options.ShowFps;
        OverlayShowHotkey.IsChecked = options.ShowHotkey;
        OverlayShowTitle.IsChecked = options.ShowTitle;
        OverlayShowGame.IsChecked = options.ShowGame;
        OverlayShowSource.IsChecked = options.ShowSource;
        OverlayShowDuration.IsChecked = options.ShowDuration;
        ShowOverlayImage(options.ImagePath);
        SetChoices(OverlayImageFitCombo,
            [new("Fit whole image", OverlayImageFit.Contain),
             new("Fill area (crop edges)", OverlayImageFit.Cover)], options.ImageFit);
        UpdateFeedbackVisibility();
        var displays = new List<Choice> { new("Main display", "") };
        displays.AddRange(ClipNotificationOverlay.Displays().Select(d => new Choice(d.Label, d.Device)));
        if (!string.IsNullOrWhiteSpace(options.Display) && !displays.Any(d => Equals(d.Value, options.Display)))
            displays.Add(new("Saved display (disconnected)", options.Display));
        SetChoices(OverlayDisplayCombo, displays, options.Display ?? "");
        SetChoices(OverlayAnchorCombo, Enum.GetValues<OverlayAnchor>().Select(a => new Choice(AnchorLabels[(int)a], a)), options.Anchor);
        SetChoices(OverlayAnimationCombo, OverlayAnimationChoices(options.Anchor), options.Animation);
        SetNumbers(OverlaySizeCombo, [48, 60, 72, 88, 104], options.Size, value => $"{value:0} DIP");
        SetNumbers(OverlayOpacityCombo, [.3, .5, .7, .9, 1], options.Opacity, value => $"{value:P0}");
        OverlayDurationBox.Text = options.Seconds.ToString("0.##", CultureInfo.CurrentCulture);
        OverlayDurationError.Text = "";
        OverlayImageError.Text = "";
        OverlayImageError.Visibility = Visibility.Collapsed;
    }

    private static Choice[] OverlayAnimationChoices(OverlayAnchor anchor)
    {
        var choices = new List<Choice>();
        if (anchor != OverlayAnchor.Center) choices.Add(new("Slide in from edge", OverlayAnimation.Slide));
        choices.AddRange([
            new("Grow / shrink", OverlayAnimation.Grow),
            new("Fade", OverlayAnimation.Fade),
            new("Ripple", OverlayAnimation.Ripple),
            new("No animation", OverlayAnimation.None),
        ]);
        return [.. choices];
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

        // Center has no edge to enter from. Refresh the combobox on anchor changes
        // so Slide is not selectable there, and use Grow when switching to Center.
        if (sender == OverlayAnchorCombo &&
            OverlayAnchorCombo.SelectedItem is Choice { Value: OverlayAnchor selectedAnchor })
        {
            OverlayAnimation current = (OverlayAnimationCombo.SelectedItem as Choice)?.Value is OverlayAnimation a
                ? a : OverlayAnimation.Grow;
            if (selectedAnchor == OverlayAnchor.Center && current == OverlayAnimation.Slide)
                current = OverlayAnimation.Grow;
            _loading = true;
            try { SetChoices(OverlayAnimationCombo, OverlayAnimationChoices(selectedAnchor), current); }
            finally { _loading = false; }
        }

        if (ClipNotificationModeCombo.SelectedItem is not Choice { Value: ClipNotificationMode mode }) return;
        settings.ClipNotificationsEnabled = ClipNotificationsCheck.IsChecked == true;
        settings.ClipEvents = new(ReplayEnabledNotice.IsChecked == true, ClipSavedNotice.IsChecked == true,
            DesktopStartedNotice.IsChecked == true, DesktopStoppedNotice.IsChecked == true, SaveFailedNotice.IsChecked == true);
        settings.ShowInactiveSaveNotifications = IdleSaveNotificationCheck.IsChecked == true;
        settings.ClipWindowsNotification = mode is ClipNotificationMode.Windows or ClipNotificationMode.Both;
        bool imageOnlyWithoutMedia = (OverlayStyleCombo.SelectedItem as Choice)?.Value is OverlayStyle.ImageOnly &&
            string.IsNullOrWhiteSpace(settings.ClipOverlay?.ImagePath);
        if (imageOnlyWithoutMedia)
        {
            OverlayImageError.Text = "Choose a local image or transparent WebM before using Image only.";
            OverlayImageError.Visibility = Visibility.Visible;
        }
        else if (OverlayImageError.Text.StartsWith("Choose a local image", StringComparison.Ordinal))
        {
            OverlayImageError.Text = "";
            OverlayImageError.Visibility = Visibility.Collapsed;
        }
        UpdateFeedbackVisibility();
        bool validDuration = QuickClip.Shell.ClipOverlayOptions.TryParseSeconds(OverlayDurationBox.Text, out double enteredSeconds);
        OverlayDurationError.Text = validDuration ? "" : "Enter a duration from 0.6 to 60 seconds.";
        if (OverlayAnchorCombo.SelectedItem is Choice { Value: OverlayAnchor anchor } &&
            OverlaySizeCombo.SelectedItem is Choice { Value: double size } &&
            OverlayOpacityCombo.SelectedItem is Choice { Value: double opacity } &&
            OverlayAnimationCombo.SelectedItem is Choice { Value: OverlayAnimation animation })
        {
            settings.ClipOverlay = (settings.ClipOverlay ?? new()) with
            {
                Enabled = mode is ClipNotificationMode.Overlay or ClipNotificationMode.Both,
                Style = (OverlayStyleCombo.SelectedItem as Choice)?.Value is OverlayStyle style ? style : OverlayStyle.Compact,
                ShowResolution = OverlayShowResolution.IsChecked == true,
                ShowFps = OverlayShowFps.IsChecked == true,
                ShowHotkey = OverlayShowHotkey.IsChecked == true,
                ImageOnly = (OverlayStyleCombo.SelectedItem as Choice)?.Value is OverlayStyle.ImageOnly,
                ImageFit = (OverlayImageFitCombo.SelectedItem as Choice)?.Value is OverlayImageFit fit
                    ? fit : OverlayImageFit.Contain,
                ShowTitle = OverlayShowTitle.IsChecked == true,
                ShowGame = OverlayShowGame.IsChecked == true,
                ShowSource = OverlayShowSource.IsChecked == true,
                ShowDuration = OverlayShowDuration.IsChecked == true,
                Display = (OverlayDisplayCombo.SelectedItem as Choice)?.Value as string,
                Anchor = anchor, Size = size, Opacity = opacity,
                Seconds = validDuration ? enteredSeconds : (settings.ClipOverlay ?? new()).Seconds,
                Animation = animation,
            };
        }
        settings.Save();
    }

    private void UpdateFeedbackVisibility()
    {
        bool enabled = ClipNotificationsCheck.IsChecked == true;
        ClipNotificationModeCombo.IsEnabled = ClipEventsPanel.IsEnabled = enabled;
        bool showOverlay = enabled && ClipNotificationModeCombo.SelectedItem is Choice
            { Value: ClipNotificationMode.Overlay or ClipNotificationMode.Both };
        if (!showOverlay) App.Host?.HideClipOverlay();
        ClipOverlayOptions.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ChooseOverlayImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose an overlay image", Filter = "Overlay animations and images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.webm" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        ClearOverlayImage.IsEnabled = false;
        try
        {
            var asset = await ClipOverlayImage.LoadAssetAsync(dialog.FileName);
            if (asset == null)
            {
                ShowOverlayImage(AppSettings.Current.ClipOverlay?.ImagePath);
                OverlayImageError.Text = Path.GetExtension(dialog.FileName).Equals(".webp", StringComparison.OrdinalIgnoreCase)
                    ? "This WebP could not be opened. Animated WebP isn't supported directly yet. Convert it to a GIF, then use Preview overlay to check the result."
                    : "This file could not be opened as an overlay. Try a PNG, JPG, GIF, or transparent WebM under 16 MB. GIFs must be 120 frames or fewer; animations must be 30 seconds or shorter.";
                OverlayImageError.Visibility = Visibility.Visible;
                return;
            }
            // Choose the animation's natural duration on import; custom seconds remain editable.
            if (asset.Frames.Length > 1)
                OverlayDurationBox.Text = Math.Clamp(asset.Frames.Sum(frame => frame.Delay.TotalSeconds),
                    QuickClip.Shell.ClipOverlayOptions.MinSeconds, QuickClip.Shell.ClipOverlayOptions.MaxSeconds)
                    .ToString("0.##", CultureInfo.CurrentCulture);
            SaveOverlayImage(dialog.FileName);
            App.Host?.TestClipOverlay();
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
        OverlayImageError.Text = "";
        OverlayImageError.Visibility = Visibility.Collapsed;
    }

    private void ShowOverlayImage(string? path)
    {
        ClearOverlayImage.IsEnabled = path != null;
        OverlayImageStatus.Text = path == null ? "" : Path.GetFileName(path);
    }

    private void ResetClipOverlay_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        settings.ClipOverlay = new() { Enabled = settings.ClipOverlay?.Enabled == true };
        settings.Save();
        bool wasLoading = _loading;
        _loading = true;
        try { LoadClipFeedback(settings); }
        finally { _loading = wasLoading; }
        App.Host?.TestClipOverlay();
    }

    private void TestClipOverlay_Click(object sender, RoutedEventArgs e) => App.Host?.TestClipOverlay();
}
