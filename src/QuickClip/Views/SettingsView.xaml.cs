using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Updates;

namespace QuickClip.Views;

public partial class SettingsView : UserControl
{
    private sealed record Choice(string Label, object Value, string Group = "", bool Enabled = true)
    {
        public override string ToString() => Label;
    }

    private readonly DispatcherTimer _restartTimer;
    private DispatcherTimer? _diagnosticsTimer;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private IReadOnlyList<CaptureEngine.AudioDevice> _mics = [];
    private readonly List<(ComboBox Combo, Button Remove)> _micRows = [];
    private IReadOnlySet<string> _encoders = new HashSet<string>();
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
        InitializeMicrophoneMeters();
        BuildHotkeyRows();
        InitializeInterfaceSettings();
        SettingsSections.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        SectionsColumn.Width = AppProfile.IsPersonal ? new GridLength(176) : new GridLength(0);
        LibraryOptions.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        if (AppProfile.IsPersonal)
        {
            SettingsContent.MaxWidth = 920;
            PersonalProfileHint.Text = $"{AppProfile.Name} {AppVersion.Current.Display} · Stable releases only; Explorer integration is disabled.";
            PersonalProfileHint.Visibility = Visibility.Visible;
            ShellCheck.Visibility = Visibility.Collapsed;
            InstallUpdateLabel.Text = PersonalInstaller.CanApply ? "Download update" : "Download verified update";
            StartupHint.Visibility = ReplayAtStartupCheck.Visibility = ReplayAtStartupHint.Visibility = Visibility.Visible;
            GamesPage.AttentionCountChanged += UpdateGameIssueBadge;
            UpdateGameIssueBadge(GamesPage.AttentionCount);
            ApplySettingsSection(RecordingSection);
            UpdateSettingsExperience();
        }
        // Recording settings restart the recorder; wait until the user stops fiddling.
        _restartTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            _restartTimer!.Stop();
            _ = Recorder.Instance.ApplySettingsAsync();
        }, Dispatcher) { IsEnabled = false };
        CaptureAdvanced.IsVisibleChanged += (_, _) => UpdateDiagnosticsTimer();
        CaptureAdvanced.Collapsed += (_, _) => UpdateDiagnosticsTimer();
        IsVisibleChanged += (_, _) =>
        {
            UpdateDiagnosticsTimer();
            if (!IsVisible && _guideActive) ExitGuide();
        };
        Unloaded += (_, _) =>
        {
            if (_guideActive) ExitGuide();
            _diagnosticsTimer?.Stop();
            Recorder.Instance.StateChanged -= OnRecorderState;
            Updater.Changed -= OnUpdaterChanged;
        };
        Loaded += (_, _) =>
        {
            Recorder.Instance.StateChanged += OnRecorderState;
            Updater.Changed += OnUpdaterChanged;
        };
    }

    private sealed record GameIssueBadge(int Count, Visibility Visibility);

    private void UpdateGameIssueBadge(int count)
    {
        GamesSection.DataContext = new GameIssueBadge(count, count == 0 ? Visibility.Collapsed : Visibility.Visible);
        System.Windows.Automation.AutomationProperties.SetHelpText(GamesSection,
            count == 0 ? "" : $"{count} game detection {(count == 1 ? "issue needs" : "issues need")} attention");
    }

    private void SettingsSection_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton section && LibraryCard != null) ApplySettingsSection(section);
    }

    private void ApplySettingsSection(RadioButton section)
    {
        bool games = section == GamesSection;
        GamesPage.Visibility = games ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = games ? Visibility.Collapsed : Visibility.Visible;
        SectionTitle.Text = section.Content.ToString();
        foreach (RadioButton item in SettingsSections.Children) item.IsTabStop = item == section;
        RecordingCard.Visibility = SectionVisibility(RecordingSection);
        AudioCard.Visibility = SectionVisibility(AudioSection);
        HotkeysCard.Visibility = SectionVisibility(HotkeysSection);
        SharingCard.Visibility = SectionVisibility(SharingSection);
        LibraryCard.Visibility = SectionVisibility(LibrarySection);
        GeneralCard.Visibility = SectionVisibility(GeneralSection);
        UpdatesCard.Visibility = Updater.IsEnabled ? SectionVisibility(GeneralSection) : Visibility.Collapsed;
        DiagnosticsCard.Visibility = SectionVisibility(DiagnosticsSection);
        if (AppProfile.IsPersonal) ApplyExperienceContent();
        GeneralPage.ScrollToTop();
        if (section == DiagnosticsSection) CaptureAdvanced.IsExpanded = true;
        if (IsLoaded && (section == LibrarySection || section == RecordingSection)) _ = RefreshRecordingFoldersAsync();
        Visibility SectionVisibility(RadioButton item) => section == item ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- updates -------------------------------------------------------------------------------

    private bool _installing;

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(ShowUpdateState);

    private void ShowUpdateState()
    {
        if (!Updater.IsEnabled) return;
        VersionText.Text = $"You have {AppProfile.Name} {AppVersion.Current.Display}" + (AppVersion.Current.IsPrerelease ? " (a nightly build)" : "") +
                           (Updater.LastChecked is DateTime t ? $" · last checked {t:t}" : "");
        CheckUpdatesButton.IsEnabled = !Updater.IsChecking && !_installing;
        var release = Updater.Available;
        UpdateActions.Visibility = release != null && !_installing ? Visibility.Visible : Visibility.Collapsed;
        if (_installing) return;
        UpdateStatusText.Text =
            Updater.IsChecking ? "Checking for updates…"
            : release != null ? $"QuickClip {release.Version.Display} is available." +
                                ((AppProfile.IsPersonal ? Updater.CanDownloadPersonal(release) : Updater.CanInstall(release)) ? "" : " Download it from the release page to install it.")
            : Updater.LastError != null ? $"Couldn't check for updates: {Updater.LastError}."
            : Updater.LastChecked != null ? "You're on the latest version."
            : "";
        InstallUpdateButton.IsEnabled = release != null && (AppProfile.IsPersonal ? Updater.CanDownloadPersonal(release) : Updater.CanInstall(release));
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updater.CheckAsync();

    private async void ThisVersionNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow window) await window.ShowWhatsNewAsync(after: null);
    }

    private async void Channel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ChannelCombo.SelectedItem is not Choice { Value: string channel } || channel == Updater.Channel) return;
        Updater.Channel = channel;
        ShowChannelHint();
        await Updater.CheckAsync(); // what's on offer depends on the channel
    }

    private void ShowChannelHint() => ChannelHint.Text = AppProfile.IsPersonal ? "Stable Personal releases." : Updater.Channel == Updater.Nightly
        ? "New features as they're made, before they're released. They may have bugs."
        : "Tested releases. A nightly build you're on now stays until a newer stable release comes out.";

    private void WhatsNew_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Updater.Available?.PageUrl ?? Updater.ReleasesPage) { UseShellExecute = true });

    private void AutoUpdate_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppSettings.Current.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        AppSettings.Current.Save();
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (Updater.Available is not { } release || _installing) return;
        using var activity = QuickClip.Shell.BackgroundActivity.Begin("Downloading update");
        _installing = true;
        UpdateActions.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                activity.Report("Downloading update", p);
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}… {p:P0}";
            });
            if (AppProfile.IsPersonal)
            {
                if (await DownloadAndApplyPersonalAsync(release, progress) is { } status)
                {
                    _installing = false;
                    UpdateProgress.IsIndeterminate = false;
                    UpdateProgress.Visibility = Visibility.Collapsed;
                    ShowUpdateState();
                    UpdateStatusText.Text = status;
                }
                return;
            }
            bool windowHidden = !(Window.GetWindow(this)?.IsVisible ?? false);
            var startSwap = await Updater.PrepareInstallAsync(release, restartInTray: windowHidden, progress, CancellationToken.None);
            UpdateStatusText.Text = "Restarting to finish the update…";
            if (!await ((App)Application.Current).QuitAsync(startSwap))
                throw new InvalidOperationException("QuickClip has to close to install the update.");
        }
        catch (Exception ex)
        {
            Log.Essential(DiagnosticCategory.Updater, "Update installation failed", ex);
            _installing = false;
            UpdateProgress.IsIndeterminate = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            ShowUpdateState();
            UpdateStatusText.Text = ex is OperationCanceledException ? "The update timed out." : "The update didn't install: " + ex.Message;
        }
    }

    internal void RefreshVisibleSection()
    {
        if (GamesPage.IsVisible) GamesPage.RefreshPresentation();
        else if (GeneralCard.IsVisible) Appearance.RefreshSelection(AppSettings.Current);
        else if (LibraryCard.IsVisible) _ = RefreshRecordingFoldersAsync();
        else Refresh();
    }

    /// <summary>Re-reads settings and devices; called whenever the page is shown.</summary>
    public async void Refresh()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var s = AppSettings.Current;
            RecordingCheck.IsChecked = s.RecordingEnabled;
            EndpointModeCheck.IsChecked = s.UseAudioEndpoints;
            ShowEndpointMode();

            _monitors = await Task.Run(Monitors.List);
            MonitorCombo.ItemsSource = _monitors.Select(m => new Choice(m.Label, m.DeviceName)).ToList();
            var savedDisplay = _monitors.FirstOrDefault(m => string.Equals(m.DeviceName, s.RecordMonitor, StringComparison.OrdinalIgnoreCase));
            // A remembered but disconnected device must never silently select a different display.
            var initialDisplay = savedDisplay ?? (string.IsNullOrWhiteSpace(s.RecordMonitor) || !AppProfile.IsPersonal
                ? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault() : null);
            if (initialDisplay != null) Select(MonitorCombo, initialDisplay.DeviceName);
            else MonitorCombo.SelectedIndex = -1;
            RefreshAreas(s.RecordAspect);

            int stepIndex = Array.FindIndex(RecordingPresets.BufferSteps, v => v >= s.BufferSeconds);
            BufferSlider.Value = stepIndex < 0 ? RecordingPresets.BufferSteps.Length - 1 : stepIndex;

            QualityCombo.ItemsSource = Enum.GetValues<RecordingQuality>().Reverse().Select(q => new Choice(RecordingPresets.Label(q), q)).ToList();
            Select(QualityCombo, s.RecordQuality);
            FpsCombo.ItemsSource = RecordingPresets.FrameRates.Select(f => new Choice($"{f} fps", f)).ToList();
            Select(FpsCombo, RecordingPresets.FrameRates.OrderBy(f => Math.Abs(f - s.RecordFps)).First());
            RefreshResolutions(s.RecordResolution);
            ResetDimensionFields();

            _encoders = await VideoEncoders.GetAvailableAsync();
            RefreshEncoderChoices(s.RecordEncoder);
            BackendCombo.ItemsSource = new Choice[] { new("Automatic", CaptureEngine.MonitorBackend.Automatic), new("Capture the visible monitor (DXGI Desktop Duplication)", CaptureEngine.MonitorBackend.DesktopDuplication), new("Capture through Windows (WGC)", CaptureEngine.MonitorBackend.GraphicsCapture) };
            Select(BackendCombo, s.CaptureBackend);
            NvencPresetCombo.ItemsSource = new Choice[]
            {
                new(AppProfile.IsPersonal ? "Performance — NVENC P2/HQ (default)" : "Performance — NVENC P2/HQ", CaptureEngine.NvencSpeed.FastestP2),
                new("Balanced — NVENC P3/HQ", CaptureEngine.NvencSpeed.FasterP3),
                new(AppProfile.IsPersonal ? "Quality — NVENC P4/HQ" : "Quality — NVENC P4/HQ (default)", CaptureEngine.NvencSpeed.BalancedP4),
            };
            Select(NvencPresetCombo, s.NvencPreset);
            DiagnosticsCheck.IsChecked = s.RecordingDiagnostics;
            CursorCheck.IsChecked = s.CaptureCursor;
            if (!AppProfile.IsPersonal) RecordingCheck.Content = "Enable QuickClip recording";
            RecordingSourceLabel.Visibility = SourceCombo.Visibility = AppProfile.IsPersonal ? Visibility.Collapsed : Visibility.Visible;
            if (AppProfile.IsPersonal) MonitorLabel.Text = "Manual desktop display";
            SourceCombo.SelectedIndex = s.PreferGameWindow ? 0 : 1;
            ShowGameWindowState();

            SplitChatCheck.IsChecked = s.SplitChatAudio;
            SplitMusicCheck.IsChecked = s.SplitMusicAudio;
            if (!AudioRouting.SeparateTracks)
            {
                foreach (var e in new FrameworkElement[] { SplitChatCheck, ChatText, SplitMusicCheck, MusicText }) e.Visibility = Visibility.Collapsed;
                AudioHint.Text = "Clips get a Desktop track with everything you hear and a track for the microphone, so you can set their " +
                                 "volumes or merge them when exporting.";
            }
            MicCheck.IsChecked = s.RecordMic;
            _mics = CaptureEngine.IsAvailable ? await Task.Run(() => { CaptureEngine.Initialize(); return CaptureEngine.EnumMicrophones(); }) : [];
            if (s.UseAudioEndpoints) await LoadEndpointsAsync();
            ShowMics(s.MicDeviceIds.Count > 0 ? s.MicDeviceIds : [""]);
            MicTracksCombo.ItemsSource = new List<Choice> { new("All on one Mic track", false), new("A track for each microphone", true) };
            Select(MicTracksCombo, s.SeparateMicTracks);

            ShowHotkeys();
            RefreshStartupSettings(s);
            StartTrayCheck.IsChecked = s.StartInTray;
            CloseTrayCheck.IsChecked = s.CloseToTray;
            SoundCheck.IsChecked = s.ClipSound;
            ConfirmDeleteCheck.IsChecked = s.ConfirmClipDeletion;
            ConfirmSaveOverCheck.IsChecked = s.ConfirmSaveOverOriginal;
            ExternalPlayerText.Text = Shell.ExternalPlayer.Label(s.ExternalPlayerPath);
            MotionBlurCheck.IsChecked = s.ShareMotionBlur;
            ShowDefaultSharing();
            ParallelMediaJobs.IsChecked = s.MediaJobConcurrency == 2;
            ShellCheck.IsChecked = SafeIsRegistered();
            if (ShellIntegration.IsMenuPackageInstalled())
            {
                ShellCheck.IsEnabled = false;
                ShellCheck.ToolTip = "On the Windows 11 right-click menu (added with scripts\\add-win11-menu.ps1; run it with -Remove to undo).";
            }
            await RefreshRecordingFoldersAsync();
            IndexLibraryCheck.IsChecked = s.EnableLibraryIndex;
            LoadClipFeedback(s);
            FilenameTemplate.Text = s.ClipFilenameTemplate;
            ExclusionsBox.Text = string.Join(Environment.NewLine, s.LibraryExclusions ?? []);
            AutoUpdateCheck.IsChecked = s.CheckForUpdates;
            ChannelCombo.ItemsSource = AppProfile.IsPersonal ? new[] { new Choice("Stable", Updater.Stable) } : new[]
            {
                new Choice("Stable", Updater.Stable),
                new Choice("Nightly (development builds)", Updater.Nightly),
            };
            Select(ChannelCombo, Updater.Channel);
            ShowChannelHint();
        }
        finally
        {
            _loading = false;
        }
        RefreshGameProfiles((GameProfileCombo.SelectedItem as QuickClip.Discovery.RecordingProfileRow)?.GameId);
        ShowUpdateState();
        UpdateCropPreview();
        UpdateEstimates();
        UpdateDiagnosticsTimer();
        OnRecorderState(Recorder.Instance.State);
        if (AppProfile.IsPersonal) UpdateSettingsExperience();
    }

    private static void Select(ComboBox combo, object? value)
    {
        foreach (var item in combo.Items)
            if (item is Choice c && Equals(c.Value, value)) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private MonitorInfo? SelectedMonitor =>
        _monitors.FirstOrDefault(m => Equals(m.DeviceName, (MonitorCombo.SelectedItem as Choice)?.Value));

    private string SelectedAspect => (AreaCombo.SelectedItem as Choice)?.Value as string ?? "";

    /// <summary>The part of the selected monitor that gets recorded.</summary>
    private RecordingArea? SelectedArea =>
        SelectedMonitor is { } m ? RecordingPresets.Area(m.Width, m.Height, SelectedAspect) : null;

#if DEBUG
    internal void SelectAspectForRender(string aspect) => Select(AreaCombo, aspect);
    internal void SetGameWindowForRender(bool on) => SourceCombo.SelectedIndex = on ? 0 : 1;

    /// <summary>Adds a second microphone row and scrolls to the microphones.</summary>
    internal void AddMicForRender()
    {
        AddMic_Click(AddMicButton, new RoutedEventArgs());
        MicTracksRow.BringIntoView();
    }
#endif

    private void ShowGameWindowState()
    {
        bool gameWindow = SourceCombo.SelectedIndex == 0;
        var selected = (EncoderCombo.SelectedItem as Choice)?.Value as string;
        NvencPresetCombo.IsEnabled = (string.IsNullOrEmpty(selected) ? RecordingPresets.DefaultEncoder(_encoders)?.Id : selected)?.EndsWith("_nvenc", StringComparison.Ordinal) == true;
        if (AppProfile.IsPersonal)
        {
            var settings = AppSettings.Current;
            DisplayPermissionCheck.Visibility = DisplayPermissionHint.Visibility = Visibility.Visible;
            DefaultDisplayPermissionsButton.Visibility = settings.AllowRecordingDisplaysByDefault ? Visibility.Collapsed : Visibility.Visible;
            ManualDesktopButton.Visibility = _guideActive ? Visibility.Collapsed : Visibility.Visible;
            var identity = SelectedMonitor?.CaptureIdentity;
            DisplayPermissionCheck.IsEnabled = identity != null;
            DisplayPermissionCheck.IsChecked = identity != null && CaptureSourcePolicy.AllowsRecordingDisplay(settings, identity);
            DisplayPermissionHint.Text = identity == null ? "Choose a connected, identifiable display."
                : DisplayPermissionCheck.IsChecked == true
                    ? "This display is allowed for automatic game recording and manual recording."
                    : "Recording is blocked on this display. Other allowed displays keep their permission.";
            BackendCombo.IsEnabled = false;
            CaptureTargetHint.Text = "Automatic recording follows an eligible game to an allowed display. QuickClip records everything visible there, including messages and notifications. Changing the recording display starts a fresh replay.";
            ManualDesktopButton.IsEnabled = settings.RecordingEnabled && (Recorder.Instance.ManualDesktopRequested ||
                identity != null && CaptureSourcePolicy.AllowsRecordingDisplay(settings, identity));
            ManualDesktopButton.IsChecked = Recorder.Instance.ManualDesktopRequested;
            return;
        }
        ManualDisplayHint.Visibility = Visibility.Collapsed;
        CaptureTargetHint.Text = gameWindow
            ? "Automatic captures a detected game window even when another window covers it. It records the selected monitor when game capture is unavailable. On Windows 10, WGC can display a yellow border. Strict game-only capture requires a future no-monitor fallback policy."
            : "Records everything visible on the selected display, including windows covering a game. The advanced monitor backend applies to this source.";
    }

    // ---- microphones ---------------------------------------------------------------------------

    private List<string> SelectedMics => _micRows.Select(r => (r.Combo.SelectedItem as Choice)?.Value as string ?? "").ToList();

    /// <summary>A row per chosen microphone. One that isn't plugged in keeps its place until it's changed or removed.</summary>
    private void ShowMics(IReadOnlyList<string> ids)
    {
        bool was = _loading;
        _loading = true;
        ClearMicrophoneMeters(_legacyMeters);
        MicList.Children.Clear();
        _micRows.Clear();
        string defaultMic = _mics.FirstOrDefault(m => m.IsDefault != 0).Name ?? "";
        foreach (string id in ids)
        {
            var choices = new List<Choice> { new(defaultMic.Length > 0 ? $"Windows default ({defaultMic})" : "Windows default", "") };
            choices.AddRange(_mics.Select(m => new Choice(m.Name, m.Id)));
            if (!choices.Any(c => Equals(c.Value, id))) choices.Add(new Choice("A microphone that isn't connected", id));
            var combo = new ComboBox { ItemsSource = choices };
            Select(combo, id);
            combo.SelectionChanged += Changed;

            int index = _micRows.Count;
            var remove = new Button
            {
                Content = "Remove", Style = (Style)FindResource("TextButton"), Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, ToolTip = "Stop recording this microphone",
            };
            remove.Click += (_, e) =>
            {
                var remaining = SelectedMics;
                remaining.RemoveAt(index);
                ShowMics(remaining);
                Changed(remove, e);
            };

            var row = new Grid { Margin = new Thickness(0, index == 0 ? 0 : 8, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(remove, 1);
            row.Children.Add(combo);
            row.Children.Add(remove);
            if (AppProfile.IsPersonal)
            {
                var input = new StackPanel();
                input.Children.Add(row);
                var meter = AddMicrophoneMeter(_legacyMeters,
                    () => (combo.SelectedItem as Choice)?.Value as string ?? "", $"microphone {index + 1}");
                input.Children.Add(meter.Panel);
                combo.SelectionChanged += (_, _) =>
                {
                    if (meter == _testingMeter) StopMicrophoneTest();
                    meter.TestResult = null;
                    meter.TestMessage = null;
                    RefreshMicrophoneMeters();
                };
                MicList.Children.Add(input);
            }
            else
                MicList.Children.Add(row);
            _micRows.Add((combo, remove));
        }
        _loading = was;
        UpdateMicControls();
        UpdateMicrophoneMonitoring();
    }

    /// <summary>The first connected microphone that isn't chosen yet (the default counts as whichever it is now).</summary>
    private string? NextMic(IReadOnlyList<string> chosen)
    {
        string? defaultId = _mics.FirstOrDefault(m => m.IsDefault != 0).Id;
        var taken = chosen.Select(id => id == "" ? defaultId : id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _mics.Select(m => m.Id).FirstOrDefault(id => !taken.Contains(id));
    }

    private void AddMic_Click(object sender, RoutedEventArgs e)
    {
        var ids = SelectedMics;
        if (NextMic(ids) is not { } next) return;
        ids.Add(next);
        ShowMics(ids);
        Changed(AddMicButton, e);
    }

    private void UpdateMicControls()
    {
        bool on = MicCheck.IsChecked == true, several = _micRows.Count > 1;
        foreach (var (combo, remove) in _micRows)
        {
            combo.IsEnabled = remove.IsEnabled = on;
            remove.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        }
        AddMicButton.Visibility = NextMic(SelectedMics) != null ? Visibility.Visible : Visibility.Collapsed;
        AddMicButton.IsEnabled = on;
        MicTracksRow.Visibility = several && !IsBasicExperience ? Visibility.Visible : Visibility.Collapsed;
        MicTracksCombo.IsEnabled = on;
    }

    private async void DisplayPermission_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedMonitor?.CaptureIdentity is { } identity)
        {
            bool allow = DisplayPermissionCheck.IsChecked == true;
            await ApplyDisplayPermissionAsync(settings => CaptureSourcePolicy.SetDisplayPermission(settings, identity, allow));
        }
        else ShowGameWindowState();
    }

    private async void DefaultDisplayPermissions_Click(object sender, RoutedEventArgs e)
    {
        if (!ModalDialog.ConfirmAction(Window.GetWindow(this),
            "Allow connected and newly connected displays by default? QuickClip records everything visible on an allowed display when a game triggers recording. Explicitly blocked displays remain blocked. You can turn off any display with the switch.",
            "Display permissions", "Use defaults", "Cancel", destructive: false)) return;
        await ApplyDisplayPermissionAsync(settings => settings.AllowRecordingDisplaysByDefault = true);
    }

    private async Task ApplyDisplayPermissionAsync(Action<AppSettings> update)
    {
        CapturePermissionCard.IsEnabled = false;
        try
        {
            if (!await Recorder.Instance.UpdateDisplayPermissionsAsync(update) && IsVisible)
                ModalDialog.Show(Window.GetWindow(this), "Could not save display permissions.", "Settings");
        }
        finally
        {
            CapturePermissionCard.IsEnabled = true;
            ShowGameWindowState();
            if (_guideActive) UpdateGuideHeader();
        }
    }

    private void ManualDesktop_Click(object sender, RoutedEventArgs e)
    {
        var recorder = Recorder.Instance;
        ManualDesktopButton.IsChecked = recorder.ManualDesktopRequested;
        if (recorder.ManualDesktopRequested) App.Host?.ToggleManualDesktop(recorder);
        else if (SelectedMonitor is { CaptureIdentity: not null } selected)
            App.Host?.SelectManualDisplay(recorder, selected);
        else App.Host?.ToggleManualDesktop(recorder);
        ShowGameWindowState();
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        if (HandleScopedVideoChange(sender)) return;
        var s = AppSettings.Current;
        if (sender == ConfirmSaveOverCheck)
        {
            bool previous = s.ConfirmSaveOverOriginal;
            s.ConfirmSaveOverOriginal = ConfirmSaveOverCheck.IsChecked == true;
            if (!s.TrySave())
            {
                s.ConfirmSaveOverOriginal = previous;
                _loading = true;
                try { ConfirmSaveOverCheck.IsChecked = previous; }
                finally { _loading = false; }
            }
            return;
        }
        if (sender == RecordingCheck && AppProfile.IsPersonal)
        {
            _restartTimer.Stop();
            _ = ChangeRecordingEnabledAsync(RecordingCheck.IsChecked == true);
            return;
        }
        if (sender == MonitorCombo && AppProfile.IsPersonal)
        {
            // This control owns only manual display selection. Do not rewrite other
            // recorder preferences or restart an automatic-game graph on selection.
            if (SelectedMonitor is not { CaptureIdentity: not null } selected) return;
            if (Recorder.Instance.ManualDesktopRequested)
                App.Host?.SelectManualDisplay(Recorder.Instance, selected, onlyIfActive: true);
            else
            {
                string previous = s.RecordMonitor;
                s.RecordMonitor = selected.DeviceName;
                if (!s.TrySave())
                {
                    s.RecordMonitor = previous;
                    ModalDialog.Show(Window.GetWindow(this), "Couldn’t save the selected display.", "Settings");
                }
            }
            var effective = EditingVideo();
            RefreshAreas(effective.Aspect);
            RefreshResolutions(effective.Resolution);
            ResetDimensionFields();
            ShowGameWindowState();
            UpdateCropPreview();
            UpdateEstimates();
            return;
        }
        if (sender == RecordingCheck) s.RecordingEnabled = RecordingCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == MonitorCombo)
            s.RecordMonitor = (MonitorCombo.SelectedItem as Choice)?.Value as string ?? "";
        if (sender == AreaCombo) s.RecordAspect = SelectedAspect;
        if (sender == MonitorCombo) RefreshAreas(s.RecordAspect);
        if (sender == MonitorCombo || sender == AreaCombo)
            RefreshResolutions(s.RecordResolution);
        if (!AppProfile.IsPersonal || sender == BufferSlider) s.BufferSeconds = RecordingPresets.BufferSteps[(int)Math.Round(BufferSlider.Value)];
        if ((!AppProfile.IsPersonal || sender == QualityCombo) && QualityCombo.SelectedItem is Choice { Value: RecordingQuality q }) s.RecordQuality = q;
        if ((!AppProfile.IsPersonal || sender == FpsCombo) && FpsCombo.SelectedItem is Choice { Value: int fps }) s.RecordFps = fps;
        if (sender == ResolutionCombo && ResolutionCombo.SelectedItem is Choice { Value: int res }) s.RecordResolution = res;
        if (sender == MonitorCombo || sender == AreaCombo || sender == ResolutionCombo)
            ResetDimensionFields();
        if (sender == EncoderCombo && EncoderCombo.SelectedItem is Choice { Enabled: true, Value: string encoder }) s.RecordEncoder = encoder;
        if ((!AppProfile.IsPersonal || sender == BackendCombo) && BackendCombo.SelectedItem is Choice { Value: CaptureEngine.MonitorBackend backend }) s.CaptureBackend = backend;
        if ((!AppProfile.IsPersonal || sender == NvencPresetCombo) && NvencPresetCombo.SelectedItem is Choice { Value: CaptureEngine.NvencSpeed preset }) s.NvencPreset = preset;
        if (!AppProfile.IsPersonal || sender == DiagnosticsCheck) s.RecordingDiagnostics = DiagnosticsCheck.IsChecked == true;
        if (sender == DiagnosticsCheck) UpdateDiagnosticsTimer();
        if (!AppProfile.IsPersonal || sender == CursorCheck) s.CaptureCursor = CursorCheck.IsChecked == true;
        if (!AppProfile.IsPersonal) s.PreferGameWindow = SourceCombo.SelectedIndex == 0;
        ShowGameWindowState();
        UpdateCropPreview();
        if (!AppProfile.IsPersonal || sender == SplitChatCheck) s.SplitChatAudio = SplitChatCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == SplitMusicCheck) s.SplitMusicAudio = SplitMusicCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == MicCheck) s.RecordMic = MicCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == AddMicButton || sender is Button || _micRows.Any(row => row.Combo == sender))
            s.MicDeviceIds = SelectedMics;
        if (!AppProfile.IsPersonal || sender == MicTracksCombo) s.SeparateMicTracks = MicTracksCombo.SelectedItem is Choice { Value: true };
        UpdateMicControls();
        UpdateBasicAudio();
        if (!AppProfile.IsPersonal || sender == StartTrayCheck) s.StartInTray = StartTrayCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == CloseTrayCheck) s.CloseToTray = CloseTrayCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == SoundCheck) s.ClipSound = SoundCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == ConfirmDeleteCheck) s.ConfirmClipDeletion = ConfirmDeleteCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == MotionBlurCheck) s.ShareMotionBlur = MotionBlurCheck.IsChecked == true;
        s.Save();

        UpdateEstimates();
        UpdateSetupPresetStatus();
        bool recordingSetting = sender != StartTrayCheck && sender != CloseTrayCheck && sender != SoundCheck
                                && sender != MotionBlurCheck && sender != ConfirmDeleteCheck && sender != ConfirmSaveOverCheck;
        if (sender == RecordingCheck || sender == SourceCombo)
        {
            _restartTimer.Stop();
            _ = Recorder.Instance.ApplySettingsAsync();
        }
        else if (recordingSetting) ScheduleRecorderRestart();
    }

    private async Task ChangeRecordingEnabledAsync(bool enabled)
    {
        RecordingCheck.IsEnabled = false;
        try
        {
            if (!await Recorder.Instance.ChangeRecordingEnabledAsync(enabled))
            {
                OnRecorderState(Recorder.Instance.State);
                if (IsVisible) ModalDialog.Show(Window.GetWindow(this), "Could not save the recording setting.", "Recording");
            }
        }
        finally { RecordingCheck.IsEnabled = true; }
    }

    private void ScheduleRecorderRestart()
    {
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void Diagnostics_Expanded(object sender, RoutedEventArgs e) => UpdateDiagnosticsTimer();

    private void UpdateDiagnosticsTimer()
    {
        if (CaptureAdvanced == null || CaptureDiagnostics == null) return;
        bool enabled = AppSettings.Current.RecordingDiagnostics;
        if (!enabled || !IsLoaded || !IsVisible || !CaptureAdvanced.IsVisible || !CaptureAdvanced.IsExpanded)
        {
            _diagnosticsTimer?.Stop();
            if (!enabled) CaptureDiagnostics.Text = "Timing sampling is off.";
            return;
        }
        if (_diagnosticsTimer?.IsEnabled == true) return;
        _diagnosticsTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
            (_, _) => RefreshDiagnostics(), Dispatcher);
        _diagnosticsTimer.Start();
        RefreshDiagnostics();
    }
    private void RefreshDiagnostics()
    {
        if (!AppSettings.Current.RecordingDiagnostics || !IsVisible || !CaptureAdvanced.IsVisible ||
            !CaptureAdvanced.IsExpanded || !CaptureEngine.IsAvailable || CaptureDiagnostics == null) return;
        try
        {
            CaptureEngine.Initialize();
            CaptureDiagnostics.Text = (Recorder.Instance.IsRunning ? CaptureEngine.VideoDiagnostics() : "Recorder off") + "\n\n" + Monitors.Diagnostics() +
                (Recorder.Instance.LastSaveTiming is { } timing ? "\n\nLatest replay save: " + timing : "");
        }
        catch (Exception ex) { CaptureDiagnostics.Text = ex.Message; }
    }

    private void FilenameTemplate_Changed(object sender, TextChangedEventArgs e)
    {
        if (FilenamePreview == null) return;
        try
        {
            var capture = new DateTimeOffset(2023, 8, 4, 6, 44, 24, TimeSpan.Zero);
            string named = ClipNaming.Stem(FilenameTemplate.Text, "PlateUp", capture, "Last round");
            string unnamed = ClipNaming.Stem(FilenameTemplate.Text, "PlateUp", capture, null);
            FilenamePreview.Text = $"{named}.mp4\n{unnamed}.mp4";
        }
        catch (ArgumentException ex) { FilenamePreview.Text = ex.Message; }
    }

    private void FilenameTemplate_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        try
        {
            _ = ClipNaming.Stem(FilenameTemplate.Text, "Game", DateTimeOffset.Now, "Title");
            AppSettings.Current.ClipFilenameTemplate = FilenameTemplate.Text.Trim();
            AppSettings.Current.Save();
        }
        catch (ArgumentException) { }
    }

    private void IndexLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (!AppProfile.IsPersonal) return;
        AppSettings.Current.EnableLibraryIndex = IndexLibraryCheck.IsChecked == true;
        AppSettings.Current.Save();
        _ = RefreshRecordingFoldersAsync();
    }

    private void Exclusions_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading) return;
        string[] folders = ExclusionsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (folders.Any(folder => Path.IsPathRooted(folder) || folder.Split('/', '\\').Any(part => part is "." or "..")))
        {
            ModalDialog.Show(Window.GetWindow(this), "Use folder paths relative to the clips folder.", "Invalid exclusion", MessageBoxButton.OK);
            return;
        }
        AppSettings.Current.LibraryExclusions = folders.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        AppSettings.Current.Save();
    }

    private void ShowDefaultSharing() =>
        DefaultSharingText.Text = $"Default · {SharingProfiles.Default(AppSettings.Current)}";

    private void SharingProfiles_Click(object sender, RoutedEventArgs e)
    {
        SharingDialogs.Manage(Window.GetWindow(this));
        ShowDefaultSharing();
    }

    private void MediaConcurrency_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        var s = AppSettings.Current;
        int next = ParallelMediaJobs.IsChecked == true ? 2 : 1;
        if (next == s.MediaJobConcurrency) return;
        int old = s.MediaJobConcurrency;
        s.MediaJobConcurrency = next;
        if (s.TrySave()) return;
        s.MediaJobConcurrency = old;
        ParallelMediaJobs.IsChecked = old == 2;
        ModalDialog.Show(Window.GetWindow(this), "Could not save the media job setting.", "QuickClip");
    }

    /// <summary>Bitrate, memory and clip size for the current choices.</summary>
    private void UpdateEstimates()
    {
        var s = AppSettings.Current;
        var effective = EditingVideo();
        BufferText.Text = RecordingPresets.DurationLabel(effective.ReplaySeconds);
        ResourceSummary.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        RamText.Visibility = AppProfile.IsPersonal ? Visibility.Collapsed : Visibility.Visible;
        ResourceVideo.Text = $"{BufferText.Text} · {effective.Fps} FPS target · {RecordingPresets.Label(effective.Quality)}";
        ResourceSize.Text = "Size estimates require a display or capture area.";
        _estimatedClipBytes = 0;
        UpdateStorageCards();
        if (SelectedArea is not { } area)
        {
            RamText.Text = BitrateText.Text = "";
            return;
        }
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, effective.Resolution);
        string encoderId = effective.SelectEncoder(_encoders, EditingGame?.Policy?.EncoderId is { Length: > 0 }) ?? effective.EncoderId;
        if (string.IsNullOrEmpty(encoderId)) encoderId = "h264_amf";
        string family = VideoEncoders.Find(encoderId)?.Family ?? "h264";
        int kbps = RecordingPresets.VideoKbps(effective.Quality, w, h, effective.Fps, family, effective.TargetKbps);
        int tracks = RecordingPresets.AudioTrackCount(s);
        long ram = RecordingPresets.EstimateRamBytes(kbps, tracks, effective.ReplaySeconds);
        long clip = Library.StorageInventory.EstimateBytes(kbps, tracks * RecordingPresets.AudioKbps, TimeSpan.FromSeconds(effective.ReplaySeconds));
        _estimatedClipBytes = clip;
        UpdateStorageCards();
        BitrateText.Text = $"≈ {kbps / 1000.0:0.#} Mbps";
        var encoder = VideoEncoders.Find(encoderId);
        string encoderLabel = effective.EncoderId.Length > 0 || effective.Codec.HasValue ? encoder?.Label ?? encoderId :
            RecordingPresets.DefaultEncoder(_encoders) is { } preferred ? $"Auto ({preferred.Label})" : "Auto";
        ResourceVideo.Text = IsBasicExperience ? $"{BufferText.Text} · {w} × {h} · {effective.Fps} FPS · {RecordingPresets.Label(effective.Quality)}" :
            $"{BufferText.Text} · {w} × {h} estimated · {effective.Fps} FPS target · {RecordingPresets.Label(effective.Quality)} · {BitrateText.Text} video · {encoderLabel}";
        ResourceSize.Text = IsBasicExperience ? $"A full-length clip takes about {Fmt.Size(clip)} on disk." :
            $"Estimated full clip {Fmt.Size(clip)} · Replay buffer about {Fmt.Size(ram)} RAM · {tracks} audio track(s)";
        RamText.Text = $"The replay buffer needs about {Fmt.Size(ram)} of memory. A full-length clip is about {Fmt.Size(clip)} on disk.";
    }

    private void OnRecorderState(RecorderState state) => Dispatcher.BeginInvoke(() =>
    {
        if (AppProfile.IsPersonal) ShowGameWindowState();
        bool recordingEnabled = AppSettings.Current.RecordingEnabled;
        if (RecordingCheck.IsChecked != recordingEnabled)
        {
            bool wasLoading = _loading;
            _loading = true;
            RecordingCheck.IsChecked = recordingEnabled;
            _loading = wasLoading;
        }
        ChatText.Text = state.ChatApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.ChatApps)}"
            : "No voice chat app is running right now.";
        MusicText.Text = (state.MusicApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.MusicApps)}."
            : "No music app is running right now.") + " Music played in a web browser stays on the Desktop track.";
        if (AppProfile.IsPersonal && state.Running && GameIndicatorStatus != null)
            GameIndicatorStatus.Text = App.Host?.RecordingIndicatorStatus ?? "Recording icons are unavailable.";
        RecorderStatus.Text = state.Running ? $"Recording · {state.Summary}"
            : state.Mode == RecordingMode.Recovering ? "Recovering display capture. " + state.Error
            : state.Error != null ? "Not recording: " + state.Error
            : state.Mode == RecordingMode.Armed ? "Waiting for a game · no capture running."
            : recordingEnabled ? "Starting…" : "Recording is off.";
    });

    // ---- general -------------------------------------------------------------------------------

    private static bool SafeIsRegistered()
    {
        try { return ShellIntegration.IsRegistered(); }
        catch { return false; }
    }

    private void ShellCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (ShellCheck.IsChecked == true) ShellIntegration.Register();
            else ShellIntegration.Unregister();
        }
        catch (Exception ex)
        {
            ModalDialog.Show(Window.GetWindow(this), "Couldn't update the Explorer menu:\n\n" + ex.Message, "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void PlayClipSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        button.IsEnabled = false;
        try
        {
            bool played = await ClipSound.PlayAsync(AppSettings.Current.ClipSoundOutputId, AppSettings.Current.ClipSoundVolume, AppSettings.Current.ClipSoundFilePath);
            if (AppProfile.IsPersonal) SoundOutputStatus.Text = played
                ? SoundOverlapMessage()
                : "The selected output could not play the chime.";
        }
        finally { button.IsEnabled = true; }
    }

}
