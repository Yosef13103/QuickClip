using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class SettingsView
{
    private IReadOnlyList<AudioEndpoint> _endpoints = [];
    private bool _loadingEndpoints;
    private string? _endpointError;
    private bool _endpointsLoaded;

    private void ShowEndpointMode()
    {
        bool selected = EndpointModeCheck.IsChecked == true;
        EndpointPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        LegacyAudioPanel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EndpointMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        bool selected = EndpointModeCheck.IsChecked == true;
        if (CommitEndpoints(settings => settings.UseAudioEndpoints = selected) && selected) await LoadEndpointsAsync();
    }

    private async void RefreshEndpoints_Click(object sender, RoutedEventArgs e) => await LoadEndpointsAsync();

    private async Task LoadEndpointsAsync()
    {
        if (_loadingEndpoints) return;
        _loadingEndpoints = true;
        RefreshDevicesButton.IsEnabled = false;
        EndpointNotice.Text = "Looking for Windows audio devices…";
        try
        {
            _endpointError = null;
            _endpoints = CaptureEngine.IsAvailable ? await Task.Run(() =>
            {
                CaptureEngine.Initialize();
                return CaptureEngine.EnumAudioEndpoints();
            }) : [];
            if (!CaptureEngine.IsAvailable) _endpointError = "The recording engine is missing.";
        }
        catch (Exception ex)
        {
            _endpointError = "Could not list audio devices. Check that the native recording engine is up to date.";
            Log.Failure(DiagnosticCategory.Audio, "Listing audio endpoints failed", ex);
            _endpoints = [];
        }
        finally
        {
            _loadingEndpoints = false;
            _endpointsLoaded = true;
            RefreshDevicesButton.IsEnabled = true;
        }
        ShowEndpointRows();
    }

    private static string EndpointName(AudioEndpoint endpoint) =>
        string.IsNullOrWhiteSpace(endpoint.Name) ? FlowName(endpoint.Flow) : endpoint.Name;

    private static string FlowName(EndpointFlow flow) => flow == EndpointFlow.Playback ? "Playback" : "Microphone";

    private void ShowEndpointRows() =>
        RenderEndpointRows(AppSettings.Current.EndpointTracks, _endpoints, _endpointsLoaded && _endpointError == null);

    internal void RenderEndpointRows(IReadOnlyList<EndpointTrack> tracks, IReadOnlyList<AudioEndpoint> endpoints, bool availabilityChecked)
    {
        ClearMicrophoneMeters(_endpointMeters);
        EndpointRows.Children.Clear();
        foreach (var flow in new[] { EndpointFlow.Playback, EndpointFlow.Microphone })
        {
            var sourceIndices = Enumerable.Range(0, tracks.Count)
                .Where(index => tracks[index].Flow == flow).ToArray();
            if (sourceIndices.Length == 0) continue;
            EndpointRows.Children.Add(new TextBlock
            {
                Text = flow == EndpointFlow.Playback ? "Playback" : "Microphones",
                Style = (Style)FindResource("SectionHeader"),
                Margin = new Thickness(0, flow == EndpointFlow.Playback ? 4 : 12, 0, 5),
            });
            foreach (int index in sourceIndices)
            {
                var track = tracks[index];
                var device = EndpointAudio.FindEndpoint(track, endpoints);
                string caption = string.IsNullOrWhiteSpace(track.Label) ? FlowName(track.Flow) : track.Label;
                string endpoint = device != null
                    ? EndpointName(device) + (device.IsDefault ? " · Windows default" : "")
                    : availabilityChecked ? "Device disconnected · selection kept" : "Device availability not checked";
                var tile = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(10, 7, 10, 8),
                    Margin = new Thickness(0, 0, 0, 5),
                    Opacity = track.Enabled ? 1 : .68,
                };
                tile.SetResourceReference(Border.BackgroundProperty,
                    track.Enabled ? "ElevatedSurfaceBrush" : "PanelBrush");
                tile.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
                var body = new StackPanel();
                tile.Child = body;

                Button Icon(string glyph, string hint, bool enabled, Action action)
                {
                    var button = EndpointButton(glyph, hint, enabled, action, icon: true);
                    button.Style = (Style)FindResource("AudioSourceIconButton");
                    return button;
                }

                var header = new DockPanel { LastChildFill = true };
                var controls = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(controls, Dock.Right);
                var onState = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(3, 0, 8, 0),
                };
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = 6, Height = 6,
                    Margin = new Thickness(0, 0, 5, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty,
                    track.Enabled ? "SuccessBrush" : "DangerBrush");
                onState.Children.Add(dot);
                var stateLabel = new TextBlock
                {
                    Text = track.Enabled ? "Enabled" : "Disabled",
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 11,
                };
                stateLabel.SetResourceReference(TextBlock.ForegroundProperty,
                    track.Enabled ? "SuccessBrush" : "DangerBrush");
                onState.Children.Add(stateLabel);
                controls.Children.Add(onState);

                MicrophoneMeter? mic = null;
                if (AppProfile.IsPersonal && flow == EndpointFlow.Microphone)
                {
                    mic = AddMicrophoneMeter(_endpointMeters, () => track.DeviceId,
                        $"microphone {caption}", compact: true);
                    mic.Test.Content = "Test mic";
                    mic.Test.ToolTip = "Record 5 seconds from this microphone, then play the sample.";
                    mic.Test.Margin = new Thickness(3, 0, 5, 0);
                    controls.Children.Add(mic.Test);
                }

                int positionInGroup = Array.IndexOf(sourceIndices, index);
                controls.Children.Add(Icon("\uE70E", $"Move {caption} up within {FlowName(flow)}",
                    positionInGroup > 0, () => MoveEndpointWithinFlow(track, -1)));
                controls.Children.Add(Icon("\uE70D", $"Move {caption} down within {FlowName(flow)}",
                    positionInGroup + 1 < sourceIndices.Length, () => MoveEndpointWithinFlow(track, 1)));
                var remove = Icon("\uE711", $"Remove {caption} from future recordings", true, () =>
                {
                    var remaining = AppSettings.Current.EndpointTracks.ToList();
                    int actual = remaining.FindIndex(t =>
                        EndpointAudio.SameDevice(t.Flow, t.DeviceId, track.Flow, track.DeviceId));
                    if (actual < 0) return;
                    remaining.RemoveAt(actual);
                    CommitEndpoints(settings => settings.EndpointTracks = remaining, rebuild: true);
                });
                remove.SetResourceReference(Control.ForegroundProperty, "DangerBrush");
                controls.Children.Add(remove);
                header.Children.Add(controls);

                var enabled = new CheckBox
                {
                    IsChecked = track.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0),
                    Width = 52, MinWidth = 52, Height = 30,
                    ToolTip = "Include this source in new recordings",
                };
                enabled.SetResourceReference(FrameworkElement.StyleProperty, "SwitchCheckBox");
                AutomationProperties.SetName(enabled, $"Record {FlowName(track.Flow)} track {caption}");
                enabled.Checked += (_, _) => UpdateEndpoint(track, t => t with { Enabled = true }, rebuild: true);
                enabled.Unchecked += (_, _) => UpdateEndpoint(track, t => t with { Enabled = false }, rebuild: true);
                DockPanel.SetDock(enabled, Dock.Left);
                header.Children.Add(enabled);

                var identity = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                var titleLine = new Grid();
                titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                titleLine.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = new TextBlock
                {
                    Text = caption,
                    FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                    ToolTip = $"Saved audio track: {caption}",
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
                titleLine.Children.Add(name);
                var input = new TextBox
                {
                    Text = track.Label,
                    Visibility = Visibility.Collapsed,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 95,
                    ToolTip = "Track name in newly recorded clips",
                };
                input.SetResourceReference(FrameworkElement.StyleProperty, "CompactBox");
                AutomationProperties.SetName(input, $"Name of {FlowName(track.Flow)} track {index + 1}");
                titleLine.Children.Add(input);

                string savedLabel = track.Label;
                var rename = Icon("\uE70F", $"Rename {caption}", true, () =>
                {
                    input.Text = savedLabel;
                    name.Visibility = Visibility.Collapsed;
                    input.Visibility = Visibility.Visible;
                    input.Focus();
                    input.SelectAll();
                });
                AutomationProperties.SetName(rename, $"Rename {FlowName(track.Flow)} track {index + 1}");
                Grid.SetColumn(rename, 1);
                titleLine.Children.Add(rename);
                void FinishRename(bool save)
                {
                    if (!ReferenceEquals(tile.Parent, EndpointRows) || input.Visibility != Visibility.Visible) return;
                    string value = input.Text.Trim();
                    input.Visibility = Visibility.Collapsed;
                    name.Visibility = Visibility.Visible;
                    if (!save) return;
                    UpdateEndpoint(track, t => t with { Label = value });
                    savedLabel = AppSettings.Current.EndpointTracks.FirstOrDefault(t =>
                        EndpointAudio.SameDevice(t.Flow, t.DeviceId, track.Flow, track.DeviceId))?.Label ?? savedLabel;
                    name.Text = string.IsNullOrWhiteSpace(savedLabel) ? FlowName(track.Flow) : savedLabel;
                }
                input.LostKeyboardFocus += (_, _) => FinishRename(save: true);
                input.KeyDown += (_, e) =>
                {
                    if (e.Key is not (Key.Enter or Key.Escape)) return;
                    e.Handled = true;
                    FinishRename(save: e.Key == Key.Enter);
                    rename.Focus();
                };
                identity.Children.Add(titleLine);
                identity.Children.Add(new TextBlock
                {
                    Text = endpoint,
                    ToolTip = endpoint,
                    Style = (Style)FindResource("Hint"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 0),
                });
                header.Children.Add(identity);
                body.Children.Add(header);

                // A single compact second line. Playback and microphone sources
                // both use the same 0–200% starting-editor-mix setting.
                var mix = new Grid { Margin = new Thickness(0, 5, 0, 0) };
                mix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
                mix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(77) });
                mix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                mix.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(49) });
                var mixTitle = new TextBlock
                {
                    Text = "Volume",
                    Style = (Style)FindResource("Hint"),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "Starting level in the editor for clips saved from now on",
                };
                Grid.SetColumn(mixTitle, 1);
                mix.Children.Add(mixTitle);
                var level = new Slider
                {
                    Minimum = 0, Maximum = 200,
                    Value = Math.Clamp(track.DefaultMixPercent, 0, 200),
                    SmallChange = 1, LargeChange = 10,
                    IsMoveToPointEnabled = true,
                    ToolTip = "Default for new clips only; older clips and original audio stems are unchanged",
                    VerticalAlignment = VerticalAlignment.Center,
                };
                level.SetResourceReference(FrameworkElement.StyleProperty, "ThemedMediaSlider");
                AutomationProperties.SetName(level, $"Default mix for new clips: {caption}");
                Grid.SetColumn(level, 2);
                mix.Children.Add(level);
                var percent = new TextBlock
                {
                    Text = $"{Math.Clamp(track.DefaultMixPercent, 0, 200)}%",
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(6, 0, 0, 0),
                };
                Grid.SetColumn(percent, 3);
                mix.Children.Add(percent);
                var saveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250),
                };
                void SaveMix()
                {
                    int desired = (int)Math.Round(level.Value);
                    if (desired != track.DefaultMixPercent) UpdateEndpointMix(track, desired);
                }
                saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveMix(); };
                level.ValueChanged += (_, _) =>
                {
                    percent.Text = $"{Math.Round(level.Value):0}%";
                    saveTimer.Stop();
                    saveTimer.Start();
                };
                tile.Unloaded += (_, _) => { saveTimer.Stop(); SaveMix(); };
                body.Children.Add(mix);
                if (mic != null)
                {
                    mic.Panel.Margin = new Thickness(50, 4, 0, 0);
                    body.Children.Add(mic.Panel);
                }
                EndpointRows.Children.Add(tile);
            }
        }

        PopulatePicker(PlaybackPicker, PlaybackPlaceholder, EndpointFlow.Playback, "Choose a playback device");
        PopulatePicker(MicrophonePicker, MicrophonePlaceholder, EndpointFlow.Microphone, "Choose a microphone");
        UpdateEndpointNotice();
        UpdateAddEndpointButton();
        UpdateMicrophoneMonitoring();
        UpdateBasicAudio();

        void PopulatePicker(ComboBox picker, TextBlock placeholder, EndpointFlow flow, string prompt)
        {
            var previous = (picker.SelectedItem as Choice)?.Value as AudioEndpoint;
            var assigned = tracks.Select(track => track.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var choices = EndpointAudio.ForFlow(endpoints, flow).Where(endpoint => !assigned.Contains(endpoint.Id))
                .OrderByDescending(endpoint => endpoint.IsDefault).ThenBy(EndpointName, StringComparer.CurrentCultureIgnoreCase)
                .Select(endpoint => new Choice(EndpointName(endpoint) + (endpoint.IsDefault ? " (Windows default)" : ""), endpoint))
                .ToArray();
            picker.ItemsSource = choices;
            picker.SelectedItem = (previous == null ? null : choices.FirstOrDefault(choice => choice.Value is AudioEndpoint endpoint &&
                EndpointAudio.SameDevice(endpoint.Flow, endpoint.Id, previous.Flow, previous.Id)))
                ?? choices.FirstOrDefault(choice => choice.Value is AudioEndpoint { IsDefault: true });
            picker.IsEnabled = tracks.Count < EndpointAudio.MaxTracks && choices.Length != 0;
            placeholder.Text = tracks.Count >= EndpointAudio.MaxTracks ? "Track limit reached" :
                availabilityChecked && choices.Length == 0 ? "No unassigned devices" : prompt;
        }
    }

    private Button EndpointButton(string caption, string accessibleName, bool enabled, Action run, bool icon = false)
    {
        var button = new Button { Content = caption, IsEnabled = enabled, Focusable = true, ToolTip = accessibleName,
            Style = (Style)FindResource(icon ? "IconButton" : "TextButton") };
        AutomationProperties.SetName(button, accessibleName);
        button.Click += (_, _) => run();
        return button;
    }

    private void EndpointPicker_Changed(object sender, SelectionChangedEventArgs e) => UpdateAddEndpointButton();

    private void UpdateAddEndpointButton()
    {
        if (AddPlaybackButton == null || AddMicrophoneButton == null || PlaybackPicker == null || MicrophonePicker == null) return;
        AddPlaybackButton.IsEnabled = PlaybackPicker.IsEnabled && PlaybackPicker.SelectedItem is Choice;
        AddMicrophoneButton.IsEnabled = MicrophonePicker.IsEnabled && MicrophonePicker.SelectedItem is Choice;
        foreach (var button in new[] { AddPlaybackButton, AddMicrophoneButton })
            button.SetResourceReference(Control.BorderBrushProperty, button.IsEnabled ? "AccentBrush" : "BorderBrush");
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var picker = sender == AddPlaybackButton ? PlaybackPicker : MicrophonePicker;
        if (settings.EndpointTracks.Count >= EndpointAudio.MaxTracks ||
            picker.SelectedItem is not Choice { Value: AudioEndpoint endpoint } ||
            settings.EndpointTracks.Any(track => StringComparer.OrdinalIgnoreCase.Equals(track.DeviceId, endpoint.Id))) return;
        CommitEndpoints(current => current.EndpointTracks =
            [.. current.EndpointTracks, new EndpointTrack(endpoint.Flow, endpoint.Id, EndpointName(endpoint))], rebuild: true);
    }

    private void MoveEndpointWithinFlow(EndpointTrack selected, int direction)
    {
        var original = AppSettings.Current.EndpointTracks;
        var reordered = EndpointAudio.ReorderWithinFlow(original, selected, direction);
        if (reordered.SequenceEqual(original)) return;
        CommitEndpoints(settings => settings.EndpointTracks = reordered, rebuild: true);
    }

    private void UpdateEndpoint(EndpointTrack selected, Func<EndpointTrack, EndpointTrack> change, bool rebuild = false)
    {
        var tracks = AppSettings.Current.EndpointTracks.ToList();
        int index = tracks.FindIndex(track => EndpointAudio.SameDevice(track.Flow, track.DeviceId, selected.Flow, selected.DeviceId));
        if (index < 0) return;
        var updated = change(tracks[index]);
        if (updated == tracks[index]) return;
        tracks[index] = updated;
        CommitEndpoints(settings => settings.EndpointTracks = tracks, rebuild);
    }

    private void UpdateEndpointMix(EndpointTrack selected, int percent)
    {
        var settings = AppSettings.Current;
        var prior = settings.EndpointTracks;
        var tracks = prior.ToList();
        int index = tracks.FindIndex(track =>
            EndpointAudio.SameDevice(track.Flow, track.DeviceId, selected.Flow, selected.DeviceId));
        if (index < 0) return;
        percent = Math.Clamp(percent, 0, 200);
        if (tracks[index].DefaultMixPercent == percent) return;
        tracks[index] = tracks[index] with { DefaultMixPercent = percent };
        settings.EndpointTracks = tracks;
        if (settings.TrySave()) return;
        settings.EndpointTracks = prior;
        ModalDialog.Show(Window.GetWindow(this),
            "Couldn't save this track's default volume. Your previous level is still saved.", "Audio");
    }

    private bool CommitEndpoints(Action<AppSettings> update, bool rebuild = false)
    {
        var settings = AppSettings.Current;
        var previous = (settings.UseAudioEndpoints, settings.EndpointTracks);
        update(settings);
        if (!settings.TrySave())
        {
            (settings.UseAudioEndpoints, settings.EndpointTracks) = previous;
            bool wasLoading = _loading;
            _loading = true;
            EndpointModeCheck.IsChecked = settings.UseAudioEndpoints;
            _loading = wasLoading;
            ShowEndpointMode();
            ShowEndpointRows();
            ModalDialog.Show(Window.GetWindow(this), "Could not save the audio change. Your previous audio choices are still active.", "Audio");
            return false;
        }
        ShowEndpointMode();
        if (rebuild) ShowEndpointRows();
        else UpdateEndpointNotice();
        UpdateBasicAudio();
        UpdateEstimates();
        ScheduleRecorderRestart();
        return true;
    }

    private void UpdateEndpointNotice()
    {
        if (_endpointError != null) { EndpointNotice.Text = _endpointError; return; }
        var plan = EndpointAudio.Plan(AppSettings.Current);
        if (plan == null) return;
        int missing = _endpointsLoaded ? plan.Count(track => EndpointAudio.FindEndpoint(track, _endpoints) == null) : 0;
        var notices = new List<string>();
        if (missing > 0) notices.Add($"{missing} enabled device(s) missing. QuickClip keeps those selections and retries the same devices without substituting a default.");
        if (_endpointsLoaded && _endpoints.Count == 0 && missing == 0) notices.Add("No active devices found.");
        EndpointNotice.Text = string.Join(" ", notices);
    }

    internal static string DescribeLane(RunningEndpointLane lane, int index)
    {
        string heading = $"{index + 1} · {FlowName(lane.Track.Flow)} · {lane.Track.Label}";
        if (lane.Status is not { } status) return $"{heading} · Waiting for status";
        string stage = status.HResult < 0 ? $"{status.Stage} failed (0x{unchecked((uint)status.HResult):X8})" : status.Stage.ToString();
        string mix = status.MixRate > 0 ? $" · mix {status.MixRate:N0} Hz / {status.MixChannels} ch / {status.MixFormat}" : "";
        string capture = status.CaptureRate > 0
            ? $" · capture {status.CaptureRate:N0} Hz / {status.CaptureChannels} ch / float32" : "";
        string age = status.MillisecondsSincePacket is { } ms ? $" · last packet {ms:N0} ms ago" : "";
        string health = status.PacketCount > 0
            ? $" · {status.SilentPackets:N0} silent · {status.Discontinuities:N0} discontinuities · {status.TimestampFallbacks:N0} timestamp fallbacks"
            : "";
        string levels = status.LastPacketPeak is { } peak && status.LastPacketRms is { } rms
            ? $" · last packet peak {peak:P0}, RMS {rms:P0} · maximum packet peak {status.MaxPacketPeak:P0}, RMS {status.MaxPacketRms:P0}"
            : "";
        return $"{heading} · {stage}{mix}{capture} · {status.PacketCount:N0} packets{age}{health}{levels}";
    }

#if DEBUG
    internal void ShowRecordingSettingsForRender(bool audio)
    {
        (audio ? AudioSection : HotkeysSection).IsChecked = true;
        if (!audio) return;
        bool wasLoading = _loading;
        _loading = true;
        EndpointModeCheck.IsChecked = true;
        _loading = wasLoading;
        ShowEndpointMode();
        RenderEndpointRows([new(EndpointFlow.Microphone, "missing-fixture", "Saved microphone")],
            [new(EndpointFlow.Playback, "render-fixture", "Virtual device", false),
             new(EndpointFlow.Microphone, "input-fixture", "Virtual device", false)], true);
    }
#endif
}
