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
    private int _guideAttempt;
    private bool IsBasicExperience => AppProfile.IsPersonal && (_guideActive || AppSettings.Current.BasicSettings);
    private int GuideStep => Math.Clamp(AppSettings.Current.GuidedSetupStep ?? 0, 0, 8);

    private static readonly (string Title, string Hint)[] GuideSteps =
    [
        ("Save the moments you want to keep", "QuickClip can remember the last few minutes while you play. Press your save shortcut to keep that moment as a clip. Your choices save as you go. You decide when to turn recording on."),
        ("Where should clips go?", "Choose a local folder for new clips. Existing videos are never moved. You can return here to change the location later."),
        ("How will you save a clip?", "Assign a shortcut you can remember. Check that Windows registered it. You can always save from the QuickClip window too."),
        ("What should your clips sound like?", "Choose computer sound and a microphone. Use Test microphone and speak to check that QuickClip can hear you."),
        ("Choose recording quality", "Set how much gameplay to keep before you press Save Clip. Higher resolution, frame rate and quality make bigger clips."),
        ("Find your games", "Let QuickClip look for installed games. Use Add Game if one is missing."),
        ("Choose which displays QuickClip can record", "Choose each display you want to allow. QuickClip records everything visible on the game's display, including other windows and messages. Manual recording also needs display permission and a separate start action."),
        ("Know when your clips are saved", "Choose a sound, notifications, and optional recording status icons. Preview the save feedback without recording. Detailed customization is available in Advanced settings."),
        ("Review and finish", "Your preferences are saved. Review the summary and choose whether to enable recording. Enabling waits for an eligible game and uses only existing display permissions. You can return to any page or finish without changing recording.")
    ];

    internal void BeginGuidedSetup()
    {
        if (!AppProfile.IsPersonal || _guideActive) return;
        var settings = AppSettings.Current;
        var previous = settings.GuidedSetupStep;
        settings.GuidedSetupStep ??= 0;
        if (!settings.TrySave())
        {
            settings.GuidedSetupStep = previous;
            ModalDialog.Show(Window.GetWindow(this), "Could not save the setup starting point. Check that QuickClip can save settings and try again.", "Guided setup");
            return;
        }
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
        SectionsColumn.Width = _guideActive ? new GridLength(0) : new GridLength(176);
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
            CropPreviewAdvanced, EncoderAdvanced, EncoderTuningAdvanced, AudioLanesAdvanced, LibraryOptions, SoundOutputPanel,
            AdvancedNotificationOptions, BitrateText, RamText })
            element.Visibility = advanced;
        SplitMusicCheck.Visibility = MusicText.Visibility = !basic && AudioRouting.SeparateTracks ? Visibility.Visible : Visibility.Collapsed;
        GamesPage.UseBasicPresentation(basic);
        CaptureAreaColumn.Width = basic ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        CaptureAreaGap.Width = new GridLength(basic ? 0 : 12);
        BasicQualityHint.Visibility = BasicAudioHint.Visibility = BasicNotificationPreview.Visibility =
            basic ? Visibility.Visible : Visibility.Collapsed;
        var settings = AppSettings.Current;
        var preferred = settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id : settings.RecordEncoder;
        BasicQualityHint.Text = _loading ? "Checking recording hardware…" :
            preferred != null && _encoders.Contains(preferred)
                ? "Recording hardware is ready."
                : "QuickClip couldn't confirm recording hardware. Check Recording in Advanced settings.";
        UpdateBasicAudio();
        UpdateEstimates();
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
        OverlayDetailsPanel.Visibility = !basic && banners && ClipSavedNotice.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        int step = GuideStep;
        var visible = Visibility.Visible;
        var hidden = Visibility.Collapsed;
        RecordingCheck.Visibility = _guideActive ? hidden : visible;
        CapturePermissionCard.Visibility = !_guideActive || step == 6 ? visible : hidden;
        RecordingQualityControls.Visibility = !_guideActive || step == 4 ? visible : hidden;
        SetupQualityPresets.Visibility = _guideActive && step == 4 ? visible : hidden;
        RefreshGameProfiles((GameProfileCombo.SelectedItem as QuickClip.Discovery.RecordingProfileRow)?.GameId);
        UpdateSetupPresetStatus();
        GeneralPreferences.Visibility = _guideActive ? hidden : visible;
        ManualDesktopButton.Visibility = _guideActive ? hidden : visible;
        if (_guideActive)
        {
            UpdatesCard.Visibility = hidden;
            if (step is 0 or 8) GeneralPage.Visibility = GamesPage.Visibility = hidden;
        }
    }

    private void UpdateBasicAudio()
    {
        if (!AppProfile.IsPersonal || BasicAudioHint == null || LegacyAudioPanel == null) return;
        bool basic = IsBasicExperience;
        AudioHint.Visibility = Visibility.Collapsed;
        EndpointDetailHint.Visibility = EndpointOrderLabel.Visibility = Visibility.Collapsed;
        EndpointModeCheck.Content = "Choose specific audio devices";
        foreach (var meter in _endpointMeters.Concat(_legacyMeters))
            meter.Save.Visibility = basic ? Visibility.Collapsed : Visibility.Visible;
        var settings = AppSettings.Current;
        BasicAudioHint.Text = "";
        if (!settings.UseAudioEndpoints && !_loading && settings.RecordMic && settings.MicDeviceIds.Any(id => id.Length == 0
                ? !_mics.Any(mic => mic.IsDefault != 0)
                : !_mics.Any(mic => string.Equals(mic.Id, id, StringComparison.OrdinalIgnoreCase))))
            BasicAudioHint.Text = "A selected microphone is missing. Connect it or choose another microphone below.";
        BasicAudioHint.Visibility = BasicAudioHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowGuideStep()
    {
        if (!_guideActive) return;
        RadioButton section = GuideStep switch
        {
            1 => LibrarySection, 2 => HotkeysSection, 3 => AudioSection,
            5 => GamesSection, 7 => NotificationsSection, _ => RecordingSection,
        };
        section.IsChecked = true;
        ApplySettingsSection(section);
        UpdateSettingsExperience();
        if (IsVisible && !_loading) GuideNext.Focus();
    }

    private void UpdateGuideHeader()
    {
        int step = GuideStep;
        GuideProgressLabel.Text = $"Step {step + 1} of {GuideSteps.Length}";
        GuideProgress.Value = step + 1;
        GuideTitle.Text = GuideSteps[step].Title;
        GuideHint.Text = GuideSteps[step].Hint;
        GuideSummaryViewport.Visibility = step == 8 ? Visibility.Visible : Visibility.Collapsed;
        GuideSummaryViewport.ItemsSource = step == 8 ? SetupSummary() : null;
        GuideBack.IsEnabled = step > 0 && !_loading && !_finishingGuide;
        GuideNext.IsEnabled = !_loading && !_finishingGuide;
        GuideSkip.IsEnabled = !_finishingGuide;
        GuideNext.Content = step == 8 ? "Finish without changing recording" : step == 0 ? "Get started" : "Continue";
        GuideEnable.Visibility = step == 8 ? Visibility.Visible : Visibility.Collapsed;
        GuideEnable.IsEnabled = !_loading && !_finishingGuide;
        GuideSkip.Visibility = step == 8 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static bool HasAuthorizedDisplay(AppSettings settings, IEnumerable<MonitorInfo> monitors) =>
        monitors.Any(monitor => monitor.CaptureIdentity is { } id && CaptureSourcePolicy.AllowsRecordingDisplay(settings, id));

    private sealed record GuideReviewItem(string Label, string Value);

    private GuideReviewItem[] SetupSummary()
    {
        var settings = AppSettings.Current;
        var shortcut = RecordingHotkeys.Read(settings, RecordingCommand.SaveClip);
        var micCount = settings.UseAudioEndpoints
            ? EndpointAudio.Plan(settings)?.Count(track => track.Flow == EndpointFlow.Microphone) ?? 0
            : settings.RecordMic ? settings.MicDeviceIds.Count : 0;
        string resolution = settings.RecordResolution == 0 ? "Match display" : $"{settings.RecordResolution}p";
        string hotkey = shortcut.IsEmpty ? "Not assigned (save from the app)" :
            $"{shortcut} · {(App.Host?.HotkeyActive == true ? "registered" : "registration not confirmed")}";
        string display = HasAuthorizedDisplay(settings, _monitors) ? "At least one display allowed at the last check" :
            "No permitted connected display. Go back to display permissions to enable recording.";
        string encoder = (settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id :
            settings.RecordEncoder) is { } id && _encoders.Contains(id) ? "Recording hardware ready" : "Recording hardware needs checking";
        return
        [
            new("Save location", settings.ClipsFolder),
            new("Clip length", RecordingPresets.DurationLabel(settings.BufferSeconds)),
            new("Video", $"{resolution} · {settings.RecordFps} FPS · {RecordingPresets.Label(settings.RecordQuality)}"),
            new("Save shortcut", hotkey),
            new("Audio", $"{micCount} microphone(s) selected"),
            new("Games", GamesPage.BasicReadiness),
            new("Displays", display),
            new("Hardware", encoder),
            new("Recording", settings.RecordingEnabled ? "Enabled" : "Off until you enable it"),
            new("Your first clip", "Play a game that QuickClip can record, then press your save shortcut or use Save Clip in the app."),
        ];
    }

    private sealed record SetupVideoPreset(string Name, int Resolution, int Fps, RecordingQuality Quality, CaptureEngine.NvencSpeed Speed);
    private static readonly SetupVideoPreset[] SetupPresets =
    [
        new("Performance", 720, 30, RecordingQuality.Medium, CaptureEngine.NvencSpeed.FastestP2),
        new("Balanced", 1080, 60, RecordingQuality.High, CaptureEngine.NvencSpeed.FastestP2),
        new("Quality", 0, 60, RecordingQuality.Ultra, CaptureEngine.NvencSpeed.FastestP2),
    ];

    private void UpdateSetupPresetStatus()
    {
        if (SetupPresetStatus == null || SetupPresetRecommendation == null || !_guideActive || GuideStep != 4) return;
        var settings = AppSettings.Current;
        string? preferred = settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id : settings.RecordEncoder;
        bool direct = preferred != null && _encoders.Contains(preferred) && !preferred.EndsWith("_mf", StringComparison.Ordinal);
        SetupPresetRecommendation.Text = _loading ? "Checking hardware…" : preferred == null || !_encoders.Contains(preferred)
            ? "No encoder confirmed yet. Check Advanced settings before enabling recording."
            : direct ? "Balanced is a starting point for a confirmed hardware encoder. Choose Performance for lower overhead."
            : "Performance is a conservative starting point for Windows compatibility encoding.";
        var active = SetupPresets.FirstOrDefault(p => p.Resolution == settings.RecordResolution && p.Fps == settings.RecordFps &&
            p.Quality == settings.RecordQuality && (!string.IsNullOrEmpty(preferred) && preferred.EndsWith("_nvenc", StringComparison.Ordinal)
                ? p.Speed == settings.NvencPreset : true));
        SetupPresetStatus.Text = active == null ? "Selected: Custom" : "Selected: " + active.Name;
    }

    private void SetupPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_guideActive || GuideStep != 4 || _loading || _finishingGuide ||
            sender is not Button { Tag: string name } || SetupPresets.FirstOrDefault(p => p.Name == name) is not { } preset) return;
        var settings = AppSettings.Current;
        var previous = (settings.RecordResolution, settings.RecordFps, settings.RecordQuality, settings.NvencPreset);
        (settings.RecordResolution, settings.RecordFps, settings.RecordQuality, settings.NvencPreset) =
            (preset.Resolution, preset.Fps, preset.Quality, preset.Speed);
        if (!settings.TrySave())
        {
            (settings.RecordResolution, settings.RecordFps, settings.RecordQuality, settings.NvencPreset) = previous;
            GuideError.Text = "Could not save recording preset.";
            return;
        }
        GuideError.Text = "";
        Refresh();
        ScheduleRecorderRestart();
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
        if (!_guideActive || _loading || _finishingGuide || GuideStep != 8) return;
        var settings = AppSettings.Current;
        string folder = settings.ClipsFolder;
        _finishingGuide = true;
        int attempt = ++_guideAttempt;
        GuideError.Text = "";
        UpdateGuideHeader();
        try
        {
            var (error, connected) = await Task.Run(() => (CheckDestination(folder), Monitors.List()));
            if (!CanFinishGuide(_guideActive && IsVisible, _guideAttempt, attempt)) return;
            if (error != null || !string.Equals(folder, settings.ClipsFolder, StringComparison.OrdinalIgnoreCase))
            {
                GuideError.Text = error ?? "The clips folder changed; review setup and try again.";
                return;
            }
            if (!HasAuthorizedDisplay(settings, connected)) { GuideError.Text = "No permitted display is connected. Check display permissions."; return; }
            string? encoder = settings.RecordEncoder.Length == 0 ? RecordingPresets.DefaultEncoder(_encoders)?.Id : settings.RecordEncoder;
            if (!CaptureEngine.IsAvailable || encoder == null || !_encoders.Contains(encoder))
            {
                GuideError.Text = "No available recording encoder was confirmed. Finish without enabling, then check Advanced settings.";
                return;
            }
            bool wasEnabled = settings.RecordingEnabled;
            if (CompleteGuide(true) && !wasEnabled) await Recorder.Instance.ApplySettingsAsync();
        }
        finally
        {
            if (attempt == _guideAttempt)
            {
                _finishingGuide = false;
                if (_guideActive) UpdateGuideHeader();
            }
        }
    }

    internal static bool CanFinishGuide(bool activeAndVisible, int currentAttempt, int completedAttempt) =>
        activeAndVisible && currentAttempt == completedAttempt;

    internal static string? CheckDestination(string folder)
    {
        try
        {
            if (QuickClip.Discovery.CatalogPaths.LocalPath(folder) is not { Length: > 3 } local ||
                new DriveInfo(Path.GetPathRoot(local)!).DriveType != DriveType.Fixed || ClipLibrary.IsMomentsPath(local))
                return "Choose a local recordings folder outside the Moments archive.";
            for (string? parent = local; parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (Directory.Exists(parent) && File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                    return "Choose a clips folder that is not redirected to another location.";
            }
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
        if (!SaveGuideCompletion(settings, enable, (current, recording) => current.TrySave(recording)))
        {
            GuideError.Text = "Could not save setup. Recording preference was not changed.";
            return false;
        }
        ExitGuide();
        OnRecorderState(Recorder.Instance.State);
        return true;
    }

    internal static bool SaveGuideCompletion(AppSettings settings, bool enable, Func<AppSettings, bool?, bool> persist)
    {
        var previous = (settings.GuidedSetupStep, settings.ReplayOnboardingCompleted);
        settings.GuidedSetupStep = null;
        settings.ReplayOnboardingCompleted = true;
        if (!persist(settings, enable ? true : null))
        {
            (settings.GuidedSetupStep, settings.ReplayOnboardingCompleted) = previous;
            return false;
        }
        if (enable) settings.RecordingEnabled = true;
        return true;
    }

    private void ExitGuide()
    {
        ++_guideAttempt;
        _finishingGuide = false;
        _guideActive = false;
        SettingsSidebar.IsEnabled = true;
        RecordingSection.IsChecked = true;
        ApplySettingsSection(RecordingSection);
        UpdateSettingsExperience();
    }
}
