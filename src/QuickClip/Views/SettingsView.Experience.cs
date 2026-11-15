using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Recording;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _guideActive, _finishingGuide;
    private bool IsBasicExperience => AppProfile.IsPersonal && (_guideActive || AppSettings.Current.BasicSettings);
    private int GuideStep => Math.Clamp(AppSettings.Current.GuidedSetupStep ?? 0, 0, 8);

    private static readonly (string Title, string Hint)[] GuideSteps =
    [
        ("Welcome to QuickClip", "QuickClip remembers your recent gameplay in memory. Press a shortcut to save a video. While waiting for a game, no capture engine runs. You choose whether to enable recording at the end. Changes you make on the following pages are saved as you go."),
        ("Where should clips go?", "Choose a local folder for new clips. Existing videos are never moved. You can return here to change the location later."),
        ("How will you save a clip?", "Assign a shortcut you can remember. Check that Windows registered it. You can always save from the QuickClip window too."),
        ("What should your clips sound like?", "Computer audio includes game and voice chat unless routed to separate playback devices. Choose your microphone and run its test if needed. You can use selected Windows devices for separate tracks or video only."),
        ("Choose recording quality", "Start with a suitable resolution and frame rate for your hardware, then choose how much history to keep. Higher settings use more memory, disk space and GPU resources. The estimate is not a performance guarantee."),
        ("Find your games", "Review installed games from the enabled libraries. Scan, add a missing game or approve a game folder. Discovery can continue in the background; never approve unrelated applications as games."),
        ("Choose what QuickClip can capture", "QuickClip currently records the whole display containing an eligible game. Other windows, messages and notifications on it may appear in clips. Review exclusions for each monitor and explicitly allow game display following if desired."),
        ("Know when your clips are saved", "Choose a sound, notifications, and optional recording status icons. Preview the save feedback without recording. Detailed customization is available in Advanced settings."),
        ("Review and finish", "Your preferences are saved. Review the summary and choose whether to enable recording. Enabling waits for an eligible game and uses only existing display permissions. You can return to any page or finish without changing recording.")
    ];

    internal void BeginGuidedSetup()
    {
        if (!AppProfile.IsPersonal || _guideActive) return;
        if (AppSettings.Current.GuidedSetupStep == null) AppSettings.Current.GuidedSetupStep = 0;
        AppSettings.Current.Save();
        _guideActive = true;
        GuideError.Text = "";
        ShowGuideStep();
    }

    private void SettingsMode_Click(object sender, RoutedEventArgs e)
    {
        if (_guideActive || _loading || !AppProfile.IsPersonal) return;
        var settings = AppSettings.Current;
        bool before = settings.BasicSettings;
        settings.BasicSettings = !before;
        if (!settings.TrySave())
        {
            settings.BasicSettings = before;
            ModalDialog.Show(Window.GetWindow(this), "Could not save the settings view preference.", "Settings");
            return;
        }
        UpdateSettingsExperience();
    }

    private void SetupGuide_Click(object sender, RoutedEventArgs e) => BeginGuidedSetup();

    private void UpdateSettingsExperience()
    {
        if (!AppProfile.IsPersonal || ExperienceHeader == null) return;
        ExperienceHeader.Visibility = Visibility.Visible;
        bool basic = IsBasicExperience;
        ModeLabel.Text = _guideActive ? "Guided setup" : basic ? "Basic settings" : "Advanced settings";
        ModeButton.Content = basic ? "Advanced settings" : "Basic settings";
        ModeButton.IsEnabled = !_guideActive && !_loading;
        SetupGuideButton.Visibility = _guideActive ? Visibility.Collapsed : Visibility.Visible;
        SetupGuideButton.Content = AppSettings.Current.GuidedSetupStep.HasValue ? "Resume setup" : "Guided setup";
        SettingsSidebar.IsEnabled = !_guideActive;
        GuideBanner.Visibility = _guideActive ? Visibility.Visible : Visibility.Collapsed;
        SharingSection.Visibility = Visibility.Visible;
        DiagnosticsSection.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        if (basic && DiagnosticsSection.IsChecked == true)
            RecordingSection.IsChecked = true;
        ApplyExperienceContent();
        if (_guideActive) UpdateGuideHeader();
    }

    private void ApplyExperienceContent()
    {
        if (!AppProfile.IsPersonal || CaptureAreaAdvanced == null) return;
        bool basic = IsBasicExperience;
        var advanced = basic ? Visibility.Collapsed : Visibility.Visible;
        foreach (FrameworkElement element in new FrameworkElement[] { CaptureAreaAdvanced, CustomSizeAdvanced,
            CropPreviewAdvanced, EncoderAdvanced, EncoderTuningAdvanced, AudioLanesAdvanced, LibraryOptions, SoundOutputPanel })
            element.Visibility = advanced;
        CaptureAreaColumn.Width = basic ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CaptureAreaGap.Width = new GridLength(basic ? 0 : 12);
        BasicQualityHint.Visibility = BasicAudioHint.Visibility = BasicNotificationPreview.Visibility =
            basic ? Visibility.Visible : Visibility.Collapsed;
        var settings = AppSettings.Current;
        var preferred = settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id : settings.RecordEncoder;
        BasicQualityHint.Text = _loading ? "Checking recording hardware…" :
            preferred != null && _encoders.Contains(preferred)
                ? "Your selected encoder is available. Detailed encoder tuning is in Advanced."
                : "Selected recording hardware is unavailable or not checked yet. Review Advanced settings if recording fails.";
        for (int i = 0; i < HotkeyRows.Children.Count && i < _hotkeyRows.Count; i++)
            HotkeyRows.Children[i].Visibility = Visibility.Visible;
        if (basic) MicTracksRow.Visibility = Visibility.Collapsed;
        else UpdateMicControls();
        // Honor existing notification channel/appearance state without writing a setting or restarting capture.
        bool image = ClipNotificationsCheck.IsChecked == true && ImageOnlyCheck.IsChecked == true;
        bool banners = ClipNotificationsCheck.IsChecked == true && ClipNotificationModeCombo.SelectedItem is Choice
            { Value: ClipNotificationMode.Overlay or ClipNotificationMode.Both };
        ImageAppearancePanel.Visibility = !basic && image ? Visibility.Visible : Visibility.Collapsed;
        BannerAppearancePanel.Visibility = !basic && banners ? Visibility.Visible : Visibility.Collapsed;
        OverlayDetailsPanel.Visibility = !basic && banners && !image ? Visibility.Visible : Visibility.Collapsed;

        int step = GuideStep;
        var visible = Visibility.Visible;
        var hidden = Visibility.Collapsed;
        RecordingCheck.Visibility = _guideActive ? hidden : visible;
        CapturePermissionCard.Visibility = !_guideActive || step == 6 ? visible : hidden;
        RecordingQualityControls.Visibility = !_guideActive || step == 4 ? visible : hidden;
        GeneralPreferences.Visibility = _guideActive ? hidden : visible;
        ManualDesktopButton.Visibility = _guideActive ? hidden : visible;
        if (_guideActive)
        {
            UpdatesCard.Visibility = hidden;
            if (step is 0 or 8) GeneralPage.Visibility = GamesPage.Visibility = hidden;
        }
    }

    private void ShowGuideStep()
    {
        if (!_guideActive) return;
        RadioButton section = GuideStep switch
        {
            1 => LibrarySection, 2 => HotkeysSection, 3 => AudioSection,
            5 => GamesSection, 7 => GeneralSection, _ => RecordingSection,
        };
        section.IsChecked = true;
        ApplySettingsSection(section);
        if (GuideStep == 6) GamePermissionOptions.IsExpanded = true;
        UpdateSettingsExperience();
    }

    private void UpdateGuideHeader()
    {
        int step = GuideStep;
        GuideProgressLabel.Text = $"Step {step + 1} of {GuideSteps.Length}";
        GuideProgress.Value = step + 1;
        GuideTitle.Text = GuideSteps[step].Title;
        GuideHint.Text = GuideSteps[step].Hint;
        GuideSummary.Text = step == 8 ? SetupSummary() : "";
        GuideBack.IsEnabled = step > 0 && !_loading && !_finishingGuide;
        GuideNext.IsEnabled = !_loading && !_finishingGuide;
        GuideSkip.IsEnabled = !_finishingGuide;
        GuideNext.Content = step == 8 ? "Finish without changing recording" : step == 0 ? "Get started" : "Continue";
        GuideEnable.Visibility = step == 8 ? Visibility.Visible : Visibility.Collapsed;
        GuideEnable.IsEnabled = !_loading && !_finishingGuide && HasAuthorizedDisplay();
        GuideSkip.Visibility = step == 8 ? Visibility.Collapsed : Visibility.Visible;
    }

    private bool HasAuthorizedDisplay() => _monitors.Any(monitor => monitor.CaptureIdentity is { } id &&
        CaptureSourcePolicy.AllowsGameDisplay(AppSettings.Current, id));

    private string SetupSummary()
    {
        var settings = AppSettings.Current;
        var shortcut = RecordingHotkeys.Read(settings, RecordingCommand.SaveClip);
        var micCount = settings.UseAudioEndpoints
            ? EndpointAudio.Plan(settings)?.Count(track => track.Flow == EndpointFlow.Microphone) ?? 0
            : settings.RecordMic ? settings.MicDeviceIds.Count : 0;
        string resolution = settings.RecordResolution == 0 ? "Native" : $"{settings.RecordResolution}p short-side limit";
        string hotkey = shortcut.IsEmpty ? "Not assigned (save from the app)" :
            $"{shortcut} · {(App.Host?.HotkeyActive == true ? "registered" : "registration not confirmed")}";
        string display = HasAuthorizedDisplay() ? "At least one connected display permitted" :
            "No permitted connected display. Go back to display permissions to enable recording.";
        string encoder = (settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id :
            settings.RecordEncoder) is { } id && _encoders.Contains(id) ? "Encoder available" : "Recording encoder unavailable or not checked";
        return $"Save location: {settings.ClipsFolder}\nClip length: {RecordingPresets.DurationLabel(settings.BufferSeconds)}\n" +
            $"Quality: {resolution} · {settings.RecordFps} FPS · {RecordingPresets.Label(settings.RecordQuality)}\n" +
            $"Save shortcut: {hotkey}\nMicrophones selected: {micCount}\n{display}\n{encoder}\n" +
            $"Recording is currently {(settings.RecordingEnabled ? "enabled" : "off")}. Discovery and actual capture still need verification.";
    }

    private void MoveGuide(int next)
    {
        if (_loading || _finishingGuide) return;
        var settings = AppSettings.Current;
        int? previous = settings.GuidedSetupStep;
        settings.GuidedSetupStep = Math.Clamp(next, 0, 8);
        if (!settings.TrySave())
        {
            settings.GuidedSetupStep = previous;
            GuideError.Text = "Could not save setup progress.";
            return;
        }
        GuideError.Text = "";
        ShowGuideStep();
    }

    private void GuideBack_Click(object sender, RoutedEventArgs e) => MoveGuide(GuideStep - 1);
    private void GuideNext_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _finishingGuide) return;
        if (GuideStep == 8) CompleteGuide(false);
        else MoveGuide(GuideStep + 1);
    }

    private void GuideSkip_Click(object sender, RoutedEventArgs e)
    {
        if (!_finishingGuide) CompleteGuide(false);
    }

    private async void GuideEnable_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _finishingGuide || GuideStep != 8 || !HasAuthorizedDisplay()) return;
        var settings = AppSettings.Current;
        string folder = settings.ClipsFolder;
        _finishingGuide = true;
        GuideError.Text = "";
        UpdateGuideHeader();
        try
        {
            string? error = await Task.Run(() => CheckDestination(folder));
            if (error != null || !string.Equals(folder, settings.ClipsFolder, StringComparison.OrdinalIgnoreCase))
            {
                GuideError.Text = error ?? "The clips folder changed; review setup and try again.";
                return;
            }
            if (!HasAuthorizedDisplay()) { GuideError.Text = "No permitted display. Review capture permissions."; return; }
            string? encoder = settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id : settings.RecordEncoder;
            if (!CaptureEngine.IsAvailable || encoder == null || !_encoders.Contains(encoder))
            {
                GuideError.Text = "No available recording encoder was confirmed. Finish without enabling, then check Advanced settings.";
                return;
            }
            if (CompleteGuide(true)) await Recorder.Instance.ApplySettingsAsync();
        }
        finally
        {
            _finishingGuide = false;
            if (_guideActive) UpdateGuideHeader();
        }
    }

    private static string? CheckDestination(string folder)
    {
        try
        {
            if (!Path.IsPathFullyQualified(folder) || ClipLibrary.IsMomentsPath(folder))
                return "Choose a local recordings folder outside the Moments archive.";
            if (!Directory.Exists(folder) && !string.Equals(folder, AppProfile.ClipsDirectory, StringComparison.OrdinalIgnoreCase))
                return "The recordings folder is unavailable. Choose another folder.";
            Directory.CreateDirectory(folder);
            using var probe = new FileStream(Path.Combine(folder, $".quickclip-write-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return "QuickClip could not write to the clips folder. Choose a writable folder and try again.";
        }
    }

    private bool CompleteGuide(bool enable)
    {
        var settings = AppSettings.Current;
        var previous = (settings.GuidedSetupStep, settings.ReplayOnboardingCompleted, settings.RecordingEnabled);
        settings.GuidedSetupStep = null;
        settings.ReplayOnboardingCompleted = true;
        if (enable) settings.RecordingEnabled = true;
        if (!settings.TrySave())
        {
            (settings.GuidedSetupStep, settings.ReplayOnboardingCompleted, settings.RecordingEnabled) = previous;
            GuideError.Text = "Could not save setup. Recording preference was not changed.";
            return false;
        }
        ExitGuide();
        OnRecorderState(Recorder.Instance.State);
        return true;
    }

    private void ExitGuide()
    {
        _guideActive = false;
        SettingsSidebar.IsEnabled = true;
        RecordingSection.IsChecked = true;
        ApplySettingsSection(RecordingSection);
        UpdateSettingsExperience();
    }
}
