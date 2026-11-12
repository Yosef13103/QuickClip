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
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private IReadOnlyList<CaptureEngine.AudioDevice> _mics = [];
    private readonly List<(ComboBox Combo, Button Remove)> _micRows = [];
    private IReadOnlySet<string> _encoders = new HashSet<string>();
    private bool _loading;
    private bool _capturingHotkey;

    public SettingsView()
    {
        InitializeComponent();
        SettingsSections.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        LibraryOptions.Visibility = AppProfile.IsPersonal ? Visibility.Visible : Visibility.Collapsed;
        if (AppProfile.IsPersonal)
        {
            PersonalProfileHint.Text = $"{AppProfile.Name} {AppVersion.Current.Display} · Updates, Windows startup and Explorer integration are disabled.";
            PersonalProfileHint.Visibility = Visibility.Visible;
            UpdatesCard.Visibility = StartupCheck.Visibility = ShellCheck.Visibility = Visibility.Collapsed;
        }
        // Recording settings restart the recorder; wait until the user stops fiddling.
        _restartTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(700), DispatcherPriority.Background, (_, _) =>
        {
            _restartTimer!.Stop();
            _ = Recorder.Instance.ApplySettingsAsync();
        }, Dispatcher) { IsEnabled = false };
        Unloaded += (_, _) =>
        {
            Recorder.Instance.StateChanged -= OnRecorderState;
            Updater.Changed -= OnUpdaterChanged;
        };
        Loaded += (_, _) =>
        {
            Recorder.Instance.StateChanged += OnRecorderState;
            Updater.Changed += OnUpdaterChanged;
        };
    }

    private void SettingsSection_Changed(object sender, RoutedEventArgs e)
    {
        if (GamesPage == null || GeneralPage == null) return;
        bool games = sender == GamesSection;
        GamesPage.Visibility = games ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = games ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- updates -------------------------------------------------------------------------------

    private bool _installing;

    private void OnUpdaterChanged() => Dispatcher.BeginInvoke(ShowUpdateState);

    private void ShowUpdateState()
    {
        if (!Updater.IsEnabled) return;
        VersionText.Text = $"You have QuickClip {AppVersion.Current.Display}" + (AppVersion.Current.IsPrerelease ? " (a nightly build)" : "") +
                           (Updater.LastChecked is DateTime t ? $" · last checked {t:t}" : "");
        CheckUpdatesButton.IsEnabled = !Updater.IsChecking && !_installing;
        var release = Updater.Available;
        UpdateActions.Visibility = release != null && !_installing ? Visibility.Visible : Visibility.Collapsed;
        if (_installing) return;
        UpdateStatusText.Text =
            Updater.IsChecking ? "Checking for updates…"
            : release != null ? $"QuickClip {release.Version.Display} is available." +
                                (Updater.CanInstall(release) ? "" : " Download it from the release page to install it.")
            : Updater.LastError != null ? $"Couldn't check for updates: {Updater.LastError}."
            : Updater.LastChecked != null ? "You're on the latest version."
            : "";
        InstallUpdateButton.IsEnabled = release != null && Updater.CanInstall(release);
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

    private void ShowChannelHint() => ChannelHint.Text = Updater.Channel == Updater.Nightly
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
        _installing = true;
        UpdateActions.Visibility = Visibility.Collapsed;
        UpdateProgress.Visibility = Visibility.Visible;
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}…";
        try
        {
            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                UpdateStatusText.Text = $"Downloading QuickClip {release.Version.Display}… {p:P0}";
            });
            bool windowHidden = !(Window.GetWindow(this)?.IsVisible ?? false);
            var startSwap = await Updater.PrepareInstallAsync(release, restartInTray: windowHidden, progress, CancellationToken.None);
            UpdateStatusText.Text = "Restarting to finish the update…";
            if (!await ((App)Application.Current).QuitAsync(startSwap))
                throw new InvalidOperationException("QuickClip has to close to install the update.");
        }
        catch (Exception ex)
        {
            Log.Write("Update failed: " + ex);
            _installing = false;
            UpdateProgress.Visibility = Visibility.Collapsed;
            ShowUpdateState();
            UpdateStatusText.Text = "The update didn't install: " + ex.Message;
        }
    }

    /// <summary>Re-reads settings and devices; called whenever the page is shown.</summary>
    public async void Refresh()
    {
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
            var initialDisplay = savedDisplay ?? (AppProfile.IsPersonal
                ? null : _monitors.FirstOrDefault(m => m.IsPrimary));
            Select(MonitorCombo, initialDisplay?.DeviceName);
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
            var auto = RecordingPresets.DefaultEncoder(_encoders);
            var encoders = new List<Choice> { new(auto != null ? $"Automatic — {auto.Label}" : "Automatic (no GPU encoder found)", "", "Automatic") };
            encoders.AddRange(RecordingPresets.RecordingEncoderOrder.Where(_encoders.Contains)
                .Select(id => new Choice(VideoEncoders.Find(id)?.Label ?? id, id, id.EndsWith("_mf", StringComparison.Ordinal) ? "Windows compatibility" : "Direct hardware encoders")));
            if (s.RecordEncoder.Length > 0 && !encoders.Any(choice => Equals(choice.Value, s.RecordEncoder)))
                encoders.Add(new Choice($"{VideoEncoders.Find(s.RecordEncoder)?.Label ?? s.RecordEncoder} (saved, unavailable)", s.RecordEncoder, "Unavailable", false));
            var encoderGroups = new System.Windows.Data.ListCollectionView(encoders);
            encoderGroups.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Choice.Group)));
            EncoderCombo.ItemsSource = encoderGroups;
            Select(EncoderCombo, s.RecordEncoder);
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
            if (AppProfile.IsPersonal) RecordingCheck.Content = "Enable QuickClip recording";
            GameSourceItem.Content = AppProfile.IsPersonal ? "Arm automatic game replay" : "Automatic - Game First";
            DisplaySourceItem.Content = AppProfile.IsPersonal ? "Manual desktop capture only" : "Selected monitor";
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

            ShowHotkey();
            StartupCheck.IsChecked = Startup.IsEnabled;
            MinTrayCheck.IsChecked = s.MinimizeToTray;
            CloseTrayCheck.IsChecked = s.CloseToTray;
            SoundCheck.IsChecked = s.ClipSound;
            ConfirmDeleteCheck.IsChecked = s.ConfirmClipDeletion;
            MotionBlurCheck.IsChecked = s.ShareMotionBlur;
            ShowShareSize();
            ShellCheck.IsChecked = SafeIsRegistered();
            if (ShellIntegration.IsMenuPackageInstalled())
            {
                ShellCheck.IsEnabled = false;
                ShellCheck.ToolTip = "On the Windows 11 right-click menu (added with scripts\\add-win11-menu.ps1; run it with -Remove to undo).";
            }
            FolderBox.Text = s.ClipsFolder;
            IndexLibraryCheck.IsChecked = s.EnableLibraryIndex;
            LoadClipFeedback(s);
            FilenameTemplate.Text = s.ClipFilenameTemplate;
            ExclusionsBox.Text = string.Join(Environment.NewLine, s.LibraryExclusions ?? []);
            AutoUpdateCheck.IsChecked = s.CheckForUpdates;
            ChannelCombo.ItemsSource = new[]
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
        ShowUpdateState();
        UpdateCropPreview();
        UpdateEstimates();
        OnRecorderState(Recorder.Instance.State);
    }

    private static void Select(ComboBox combo, object? value)
    {
        foreach (var item in combo.Items)
            if (item is Choice c && Equals(c.Value, value)) { combo.SelectedItem = item; return; }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private MonitorInfo? SelectedMonitor =>
        _monitors.FirstOrDefault(m => Equals(m.DeviceName, (MonitorCombo.SelectedItem as Choice)?.Value)) ?? _monitors.FirstOrDefault();

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
            var display = _monitors.FirstOrDefault(m => Equals(m.DeviceName, (MonitorCombo.SelectedItem as Choice)?.Value));
            bool authorized = !string.IsNullOrEmpty(settings.AuthorizedGameDisplay);
            GameDisplayConsent.Visibility = gameWindow ? Visibility.Visible : Visibility.Collapsed;
            GameDisplayConsent.IsEnabled = authorized || display?.CaptureIdentity != null;
            GameDisplayConsent.Content = authorized ? "Revoke game display permission" : "Allow selected display during games…";
            BackendCombo.IsEnabled = false;
            CaptureTargetHint.Text = gameWindow
                ? "Waits for an eligible game. With display permission, Desktop Duplication records everything visible on that display while the game is open."
                    + (authorized ? " Permission is set for the previously confirmed display." : " No display permission has been granted.")
                : "No automatic capture. Use Start manual desktop capture in the tray to explicitly record the selected display.";
            return;
        }
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
            MicList.Children.Add(row);
            _micRows.Add((combo, remove));
        }
        _loading = was;
        UpdateMicControls();
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
        MicTracksRow.Visibility = several ? Visibility.Visible : Visibility.Collapsed;
        MicTracksCombo.IsEnabled = on;
    }

    private void GameDisplayConsent_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        if (settings.AuthorizedGameDisplay != null) settings.AuthorizedGameDisplay = null;
        else
        {
            var display = _monitors.FirstOrDefault(m => Equals(m.DeviceName, (MonitorCombo.SelectedItem as Choice)?.Value));
            if (display?.CaptureIdentity == null || ModalDialog.Show(Window.GetWindow(this),
                $"Allow game-triggered recording of everything visible on {display.Label}? Other apps, messages and notifications on this display can appear in saved clips.",
                "Allow entire-display game recording?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (!CaptureSourcePolicy.AcceptGameDisplay(settings, display, confirmed: true)) return;
        }
        settings.Save();
        ShowGameWindowState();
        _ = Recorder.Instance.ApplySettingsAsync();
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        var s = AppSettings.Current;
        s.RecordingEnabled = RecordingCheck.IsChecked == true;
        if (!AppProfile.IsPersonal || sender == MonitorCombo)
            s.RecordMonitor = (MonitorCombo.SelectedItem as Choice)?.Value as string ?? "";
        if (sender == AreaCombo) s.RecordAspect = SelectedAspect;
        if (sender == MonitorCombo) RefreshAreas(s.RecordAspect);
        if (sender == MonitorCombo || sender == AreaCombo)
            RefreshResolutions(s.RecordResolution);
        s.BufferSeconds = RecordingPresets.BufferSteps[(int)Math.Round(BufferSlider.Value)];
        if (QualityCombo.SelectedItem is Choice { Value: RecordingQuality q }) s.RecordQuality = q;
        if (FpsCombo.SelectedItem is Choice { Value: int fps }) s.RecordFps = fps;
        if (sender == ResolutionCombo && ResolutionCombo.SelectedItem is Choice { Value: int res }) s.RecordResolution = res;
        if (sender == MonitorCombo || sender == AreaCombo || sender == ResolutionCombo)
            ResetDimensionFields();
        if (sender == EncoderCombo && EncoderCombo.SelectedItem is Choice { Enabled: true, Value: string encoder }) s.RecordEncoder = encoder;
        if (BackendCombo.SelectedItem is Choice { Value: CaptureEngine.MonitorBackend backend }) s.CaptureBackend = backend;
        if (NvencPresetCombo.SelectedItem is Choice { Value: CaptureEngine.NvencSpeed preset }) s.NvencPreset = preset;
        s.RecordingDiagnostics = DiagnosticsCheck.IsChecked == true;
        s.CaptureCursor = CursorCheck.IsChecked == true;
        s.PreferGameWindow = SourceCombo.SelectedIndex == 0;
        ShowGameWindowState();
        UpdateCropPreview();
        s.SplitChatAudio = SplitChatCheck.IsChecked == true;
        s.SplitMusicAudio = SplitMusicCheck.IsChecked == true;
        s.RecordMic = MicCheck.IsChecked == true;
        s.MicDeviceIds = SelectedMics;
        s.SeparateMicTracks = MicTracksCombo.SelectedItem is Choice { Value: true };
        UpdateMicControls();
        s.MinimizeToTray = MinTrayCheck.IsChecked == true;
        s.CloseToTray = CloseTrayCheck.IsChecked == true;
        s.ClipSound = SoundCheck.IsChecked == true;
        s.ConfirmClipDeletion = ConfirmDeleteCheck.IsChecked == true;
        s.ShareMotionBlur = MotionBlurCheck.IsChecked == true;
        s.Save();

        if (sender == StartupCheck)
        {
            try { Startup.Set(StartupCheck.IsChecked == true); }
            catch (Exception ex) { Log.Write("Changing startup failed: " + ex.Message); }
        }

        UpdateEstimates();
        bool recordingSetting = sender != StartupCheck && sender != MinTrayCheck && sender != CloseTrayCheck && sender != SoundCheck
                                && sender != MotionBlurCheck && sender != ConfirmDeleteCheck;
        if (sender == RecordingCheck || sender == SourceCombo)
        {
            _restartTimer.Stop();
            _ = Recorder.Instance.ApplySettingsAsync();
        }
        else if (recordingSetting) ScheduleRecorderRestart();
    }

    private void ScheduleRecorderRestart()
    {
        _restartTimer.Stop();
        _restartTimer.Start();
    }

    private void Diagnostics_Expanded(object sender, RoutedEventArgs e) => RefreshDiagnostics();
    private void Diagnostics_Refresh(object sender, RoutedEventArgs e) => RefreshDiagnostics();
    private void RefreshDiagnostics()
    {
        if (!CaptureEngine.IsAvailable || CaptureDiagnostics == null) return;
        try
        {
            CaptureEngine.Initialize();
            CaptureDiagnostics.Text = (Recorder.Instance.IsRunning ? CaptureEngine.VideoDiagnostics() : "Recorder off") + "\n\n" + Monitors.Diagnostics();
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

    private void ShowShareSize() =>
        ShareSizeBox.Text = AppSettings.Current.ShareSizeMB.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    private void ShareSize_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) ShowShareSize();
        if (e.Key is Key.Enter or Key.Escape) Keyboard.ClearFocus();
    }

    /// <summary>The share size limit, from 1 MB up; anything that isn't a number goes back to what it was.</summary>
    private void ShareSize_Commit(object sender, KeyboardFocusChangedEventArgs e)
    {
        var s = AppSettings.Current;
        if (double.TryParse(ShareSizeBox.Text.Replace("MB", "", StringComparison.OrdinalIgnoreCase).Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var mb) && mb > 0)
        {
            s.ShareSizeMB = Math.Clamp(Math.Round(mb, 1), 1, 4000);
            s.Save();
        }
        ShowShareSize();
    }

    /// <summary>Bitrate, memory and clip size for the current choices.</summary>
    private void UpdateEstimates()
    {
        var s = AppSettings.Current;
        BufferText.Text = RecordingPresets.DurationLabel(s.BufferSeconds);
        if (SelectedArea is not { } area)
        {
            RamText.Text = BitrateText.Text = "";
            return;
        }
        var (w, h) = RecordingPresets.OutputSize(area.Width, area.Height, s.RecordResolution);
        string encoderId = s.RecordEncoder.Length > 0 ? s.RecordEncoder : RecordingPresets.DefaultEncoder(_encoders)?.Id ?? "h264_amf";
        string family = VideoEncoders.Find(encoderId)?.Family ?? "h264";
        int kbps = RecordingPresets.VideoKbps(s.RecordQuality, w, h, s.RecordFps, family);
        int tracks = RecordingPresets.AudioTrackCount(s);
        long ram = RecordingPresets.EstimateRamBytes(kbps, tracks, s.BufferSeconds);
        long clip = (long)((kbps + tracks * RecordingPresets.AudioKbps) * 1000.0 / 8 * s.BufferSeconds);
        BitrateText.Text = $"≈ {kbps / 1000.0:0.#} Mbps";
        RamText.Text = $"The replay buffer needs about {Fmt.Size(ram)} of memory. A full-length clip is about {Fmt.Size(clip)} on disk.";
    }

    private void OnRecorderState(RecorderState state) => Dispatcher.BeginInvoke(() =>
    {
        ChatText.Text = state.ChatApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.ChatApps)}"
            : "No voice chat app is running right now.";
        MusicText.Text = (state.MusicApps.Count > 0
            ? $"Detected now: {string.Join(", ", state.MusicApps)}."
            : "No music app is running right now.") + " Music played in a web browser stays on the Desktop track.";
        RecorderStatus.Text = state.Running ? $"Recording · {state.Summary}"
            : state.Error != null ? "Not recording: " + state.Error
            : state.Mode == RecordingMode.Armed ? "Armed · no recording active."
            : AppSettings.Current.RecordingEnabled ? "Starting…" : "Recording is off.";
    });

    // ---- hotkey --------------------------------------------------------------------------------

    private void ShowHotkey()
    {
        var s = AppSettings.Current;
        HotkeyButton.Content = new Hotkey(s.HotkeyModifiers, s.HotkeyKey).ToString();
        HotkeyStatus.Text = s.HotkeyKey == 0 ? "Click to assign a shortcut."
            : App.Host?.HotkeyActive == false
            ? "Another app is already using this shortcut — pick a different one."
            : "Works everywhere, including in games.";
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        App.Host?.SuspendHotkey();
        HotkeyButton.Content = "Press the keys…";
        HotkeyStatus.Text = "Esc cancels, Backspace clears.";
        HotkeyButton.Focus();
    }

    private void HotkeyButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            EndHotkeyCapture();
            return;
        }
        if (Hotkey.IsModifierKey(key)) return;

        var hotkey = key is Key.Back or Key.Delete ? new Hotkey(0, 0) : Hotkey.FromWpf(Keyboard.Modifiers, key);
        var result = App.Host?.SetHotkey(RecordingCommand.SaveClip, hotkey);
        if (result == null)
        {
            RecordingHotkeys.Write(AppSettings.Current, RecordingCommand.SaveClip, hotkey);
            AppSettings.Current.Save();
        }
        EndHotkeyCapture();
        if (result is HotkeyRegistration.Duplicate or HotkeyRegistration.Unavailable or HotkeyRegistration.Invalid)
            HotkeyStatus.Text = result == HotkeyRegistration.Duplicate ? "QuickClip already uses this shortcut."
                : "Windows could not register this shortcut. The previous binding was kept.";
    }

    private void HotkeyButton_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_capturingHotkey) EndHotkeyCapture();
    }

    private void EndHotkeyCapture()
    {
        _capturingHotkey = false;
        App.Host?.RegisterHotkey();
        ShowHotkey();
    }

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

    private void PlayClipSound_Click(object sender, RoutedEventArgs e) => ClipSound.Play();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should clips be saved?", InitialDirectory = AppSettings.Current.ClipsFolder };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (ClipLibrary.IsMomentsPath(dlg.FolderName))
        {
            ModalDialog.Show(Window.GetWindow(this), "Choose a clips folder outside the Moments archive.", "Protected archive", MessageBoxButton.OK);
            return;
        }
        AppSettings.Current.ClipsFolder = dlg.FolderName;
        AppSettings.Current.Save();
        FolderBox.Text = dlg.FolderName;
    }
}
