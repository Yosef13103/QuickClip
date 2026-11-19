using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Discovery;
using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class SettingsView
{
    private bool _loadingGameProfiles;
    private System.Windows.Threading.DispatcherTimer? _profileRefreshTimer;

    private void ScheduleActiveProfileRefresh()
    {
        if (_profileRefreshTimer == null)
        {
            _profileRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _profileRefreshTimer.Tick += (_, _) =>
            {
                _profileRefreshTimer.Stop();
                _ = Recorder.Instance.RefreshActiveProfileAsync();
            };
        }
        _profileRefreshTimer.Stop();
        _profileRefreshTimer.Start();
    }
    private RecordingProfileRow? EditingGame =>
        GameProfilePanel?.Visibility == Visibility.Visible && GameProfileCombo?.IsEnabled == true &&
        GameProfileCombo.SelectedItem is RecordingProfileRow { Kind: RecordingProfileRowKind.Game, Enabled: true } row ? row : null;

    private RecordingVideoOptions EditingVideo()
    {
        var policy = EditingGame?.Policy;
        // Existing draft rate-control modes cannot be produced by the current
        // native encoder. Show the supported preview and require explicit repair.
        if (policy?.Bitrate?.Mode is GameRateControl.Cbr or GameRateControl.Vbr or GameRateControl.Cqp)
            policy = policy with { Bitrate = null };
        return RecordingVideoOptions.Resolve(AppSettings.Current, policy);
    }

    private void RefreshGameProfiles(string? selectedId = null)
    {
        if (GameProfilePanel == null || _loadingGameProfiles) return;
        GameProfilePanel.Visibility = AppProfile.IsPersonal && !_guideActive && !IsBasicExperience
            ? Visibility.Visible : Visibility.Collapsed;
        if (GameProfilePanel.Visibility != Visibility.Visible)
        {
            RefreshProfileControls();
            return;
        }
        var loaded = GameProfileStore.ForCurrentProfile().Load();
        bool readable = loaded.Status is GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Missing or GameProfileLoadStatus.Migrated;
        _loadingGameProfiles = true;
        try
        {
            var rows = RecordingProfileSelection.Build(GameCatalogRuntime.Rows(AppSettings.Current.Games), loaded.Document);
            GameProfileCombo.ItemsSource = rows;
            GameProfileCombo.IsEnabled = readable;
            GameProfileCombo.SelectedItem = readable
                ? rows.FirstOrDefault(row => row.Enabled && selectedId != null && row.GameId == selectedId) ?? rows[0]
                : rows[0];
            GameProfileStatus.Text = readable ? "" : "Profile file is invalid or unavailable. It will not be overwritten.";
            RefreshGameProfileEditor();
        }
        finally { _loadingGameProfiles = false; }
    }

    private void GameProfile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingGameProfiles && !_loading && IsInitialized) RefreshGameProfileEditor();
    }

    private void RefreshGameProfileEditor()
    {
        var row = EditingGame;
        GameOverridePanel.Visibility = GameBitratePanel.Visibility =
            row != null ? Visibility.Visible : Visibility.Collapsed;
        if (row != null)
        {
            GameProfileStatus.Text = "Changes to the game being recorded start a fresh replay buffer. Other games and manual desktop are unaffected.";
        }
        else if (GameProfileCombo.IsEnabled)
            GameProfileStatus.Text = "All Games — default recording settings; manual desktop uses these settings.";
        RefreshProfileControls();
    }

    private void RefreshProfileControls()
    {
        // Constructor and asynchronous device refresh also call this method.
        if (AreaCombo == null || GameBitratePanel == null || EncoderCombo.ItemsSource == null ||
            QualityCombo.ItemsSource == null || FpsCombo.ItemsSource == null) return;
        var row = EditingGame;
        var p = row?.Policy;
        var effective = EditingVideo();
        bool was = _loading;
        _loading = true;
        try
        {
            RefreshAreas(effective.Aspect);
            RefreshResolutions(effective.Resolution);
            int step = Array.FindIndex(RecordingPresets.BufferSteps, v => v >= effective.ReplaySeconds);
            BufferSlider.Value = step < 0 ? RecordingPresets.BufferSteps.Length - 1 : step;
            Select(QualityCombo, effective.Quality);
            Select(FpsCombo, effective.Fps);
            string encoder = effective.Codec.HasValue
                ? effective.SelectEncoder(_encoders, false) ?? "" : effective.EncoderId;
            RefreshEncoderChoices(encoder);
            Select(NvencPresetCombo, effective.NvencPreset);
            GameBitrateBox.Text = p?.Bitrate is { Mode: GameRateControl.CustomBitrate, Kbps: int kbps }
                ? kbps.ToString(CultureInfo.InvariantCulture) : "";
            GameBitrateHint.Text = p?.Bitrate?.Mode switch
            {
                GameRateControl.CustomBitrate => "Override · target video bitrate (kbps).",
                GameRateControl.Automatic => "Override · automatic bitrate.",
                null => "Inherited · automatic bitrate based on quality.",
                _ => "Unsupported saved rate-control mode. Set a target or Auto, or reset this field.",
            };
            SetField(AreaLabel, "Capture area", p?.Aspect != null, ResetAspectOverride);
            SetField(ResolutionLabel, "Output resolution", p?.ResolutionShortSide != null, ResetResolutionOverride);
            SetField(BufferLabel, "Clip length", p?.ReplaySeconds != null, ResetBufferOverride);
            SetField(QualityLabel, "Quality", p?.Quality != null, ResetQualityOverride);
            SetField(FpsLabel, "Frame rate", p?.Fps != null, ResetFpsOverride);
            SetField(EncoderLabel, "Encoder", p?.EncoderId != null || p?.Codec != null, ResetEncoderOverride);
            SetField(NvencLabel, "NVENC performance preset", p?.NvencPreset != null, ResetNvencOverride);
            ResetBitrateOverride.Visibility = row != null && p?.Bitrate != null ? Visibility.Visible : Visibility.Collapsed;
            ResetProfileButton.IsEnabled = row?.HasOverrides == true;
            GameOverridePanel.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
            GameBitratePanel.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
            ResetDimensionFields();
            ShowGameWindowState();
            UpdateCropPreview();
            UpdateEstimates();
        }
        finally { _loading = was; }

        void SetField(TextBlock label, string name, bool overridden, Button reset)
        {
            label.Text = name;
            reset.Visibility = row != null ? Visibility.Visible : Visibility.Collapsed;
            reset.Content = overridden ? "\uE72C" : "\uE718";
            reset.SetResourceReference(Control.ForegroundProperty, overridden ? "AccentBrush" : "SubtleText");
            reset.Opacity = overridden ? 1 : 0.6;
            reset.ToolTip = overridden ? $"{name}: customized. Reset to All Games." : $"{name}: follows All Games. Click to keep the current value independently.";
            label.ToolTip = row == null ? null : overridden ? "Customized for this game" : "Follows All Games";
            System.Windows.Automation.AutomationProperties.SetName(reset,
                overridden ? $"Reset {name} override" : $"Keep {name} independent of All Games");
        }
    }

    private void RefreshEncoderChoices(string selected)
    {
        var auto = RecordingPresets.DefaultEncoder(_encoders);
        var choices = new List<Choice> { new(auto != null ? $"Automatic — {auto.Label}" : "Automatic (no GPU encoder found)", "", "Automatic") };
        choices.AddRange(RecordingPresets.RecordingEncoderOrder.Where(_encoders.Contains)
            .Select(id => new Choice(VideoEncoders.Find(id)?.Label ?? id, id,
                id.EndsWith("_mf", StringComparison.Ordinal) ? "Windows compatibility" : "Direct hardware encoders")));
        if (selected.Length > 0 && choices.All(c => !Equals(c.Value, selected)))
            choices.Add(new Choice($"{VideoEncoders.Find(selected)?.Label ?? selected} (saved, unavailable)", selected, "Unavailable", false));
        var groups = new System.Windows.Data.ListCollectionView(choices);
        groups.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(Choice.Group)));
        EncoderCombo.ItemsSource = groups;
        Select(EncoderCombo, selected);
    }

    private bool SaveProfileChange(Func<GameRecordingPolicy, GameRecordingPolicy> change)
    {
        if (_loadingGameProfiles || _loading || EditingGame is not { } row) return false;
        try
        {
            if (AppSettings.SaveDisabled) throw new InvalidOperationException("Settings writes are disabled.");
            var store = GameProfileStore.ForCurrentProfile();
            var loaded = store.Load();
            if (loaded.Status is not (GameProfileLoadStatus.Loaded or GameProfileLoadStatus.Missing or GameProfileLoadStatus.Migrated))
                throw new InvalidOperationException("Profile file is invalid or unavailable.");
            var fresh = RecordingProfileSelection.Build(GameCatalogRuntime.Rows(AppSettings.Current.Games), loaded.Document)
                .SingleOrDefault(choice => choice.GameId == row.GameId);
            // Structural comparison includes immutable-list fields read from disk.
            if (fresh is not { Enabled: true } || fresh.ProfileKey != row.ProfileKey ||
                JsonSerializer.Serialize(fresh.Policy) != JsonSerializer.Serialize(row.Policy))
                throw new InvalidOperationException("The game or profile changed. Refresh before saving.");
            var policy = change(fresh.Policy ?? new()) with { Tags = null };
            policy.Validate();
            store.Save(RecordingProfileSelection.Change(loaded.Document, fresh, policy));
            RefreshGameProfiles(row.GameId);
            ScheduleActiveProfileRefresh();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            RefreshGameProfiles(row.GameId);
            GameProfileStatus.Text = ex.Message;
            return false;
        }
    }

    // Return true for every scoped control, even on invalid input. A failed
    // profile write must never fall through into global settings mutation.
    private bool HandleScopedVideoChange(object sender)
    {
        if (EditingGame == null) return false;
        if (sender == AreaCombo) SaveProfileChange(p => p with { Aspect = SelectedAspect });
        else if (sender == ResolutionCombo)
        {
            if (ResolutionCombo.SelectedItem is Choice { Value: int res }) SaveProfileChange(p => p with { ResolutionShortSide = res });
        }
        else if (sender == BufferSlider) SaveProfileChange(p => p with { ReplaySeconds = RecordingPresets.BufferSteps[(int)Math.Round(BufferSlider.Value)] });
        else if (sender == QualityCombo)
        {
            if (QualityCombo.SelectedItem is Choice { Value: RecordingQuality q }) SaveProfileChange(p => p with { Quality = q });
        }
        else if (sender == FpsCombo)
        {
            if (FpsCombo.SelectedItem is Choice { Value: int fps }) SaveProfileChange(p => p with { Fps = fps });
        }
        else if (sender == EncoderCombo)
        {
            if (EncoderCombo.SelectedItem is Choice { Enabled: true, Value: string id }) SaveProfileChange(p => p with { EncoderId = id, Codec = null });
        }
        else if (sender == NvencPresetCombo)
        {
            if (NvencPresetCombo.SelectedItem is Choice { Value: CaptureEngine.NvencSpeed preset }) SaveProfileChange(p => p with { NvencPreset = preset });
        }
        else return false;
        return true;
    }

    private void ResetGameField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;
        var effective = EditingVideo();
        SaveProfileChange(p => field switch
        {
            "Aspect" => p with { Aspect = p.Aspect == null ? effective.Aspect : null },
            "Resolution" => p with { ResolutionShortSide = p.ResolutionShortSide == null ? effective.Resolution : null },
            "Buffer" => p with { ReplaySeconds = p.ReplaySeconds == null ? effective.ReplaySeconds : null },
            "Quality" => p with { Quality = p.Quality == null ? effective.Quality : null },
            "Fps" => p with { Fps = p.Fps == null ? effective.Fps : null },
            "Encoder" => p with { EncoderId = p.EncoderId == null && p.Codec == null ? effective.EncoderId : null, Codec = null },
            "Nvenc" => p with { NvencPreset = p.NvencPreset == null ? effective.NvencPreset : null },
            "Bitrate" => p with { Bitrate = null },
            _ => p,
        });
    }

    private void ResetGameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (EditingGame is not { } row || Window.GetWindow(this) is not { } owner) return;
        if (ModalDialog.AskConfirmation(owner, $"Reset all overrides for {row.Label}? Other games and existing clips are unchanged.",
            "Reset recording profile", "Reset profile"))
            SaveProfileChange(_ => new());
    }

    private void SaveGameBitrate_Click(object sender, RoutedEventArgs e)
    {
        string text = GameBitrateBox.Text.Trim();
        if (text.Length == 0) { SaveProfileChange(p => p with { Bitrate = new(GameRateControl.Automatic) }); return; }
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int kbps) ||
            kbps is < RecordingPresets.MinTargetVideoKbps or > RecordingPresets.MaxTargetVideoKbps)
        {
            GameBitrateHint.Text = $"Enter {RecordingPresets.MinTargetVideoKbps}–{RecordingPresets.MaxTargetVideoKbps} kbps, or leave blank for Auto.";
            return;
        }
        SaveProfileChange(p => p with { Bitrate = new(GameRateControl.CustomBitrate, kbps) });
    }

}
