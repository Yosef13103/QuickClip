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
    private int _statusRequest;

    private void ShowEndpointMode()
    {
        bool selected = EndpointModeCheck.IsChecked == true;
        EndpointPanel.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        LegacyAudioPanel.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void EndpointMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsInitialized) return;
        AppSettings.Current.UseAudioEndpoints = EndpointModeCheck.IsChecked == true;
        ShowEndpointMode();
        CommitEndpoints();
        if (AppSettings.Current.UseAudioEndpoints) await LoadEndpointsAsync();
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
            Log.Write("Listing audio endpoints failed: " + ex.Message);
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
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            int index = i;
            var device = EndpointAudio.FindEndpoint(track, endpoints);
            string caption = string.IsNullOrWhiteSpace(track.Label) ? FlowName(track.Flow) : track.Label;
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(EndpointButton("Up", $"Move {caption} up", index > 0, () => MoveEndpoint(index, -1)));
            actions.Children.Add(EndpointButton("Down", $"Move {caption} down", index + 1 < tracks.Count, () => MoveEndpoint(index, 1)));
            actions.Children.Add(EndpointButton("Remove", $"Remove {caption}", true, () =>
            {
                var remaining = AppSettings.Current.EndpointTracks.ToList();
                remaining.RemoveAt(index);
                AppSettings.Current.EndpointTracks = remaining;
                CommitEndpoints(rebuild: true);
            }));
            row.Children.Add(actions);

            var enabled = new CheckBox { IsChecked = track.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Record this device" };
            AutomationProperties.SetName(enabled, $"Record {FlowName(track.Flow)} track {caption}");
            enabled.Checked += (_, _) => UpdateEndpoint(track, t => t with { Enabled = true });
            enabled.Unchecked += (_, _) => UpdateEndpoint(track, t => t with { Enabled = false });
            row.Children.Add(enabled);

            var fields = new StackPanel { Margin = new Thickness(8, 0, 4, 0) };
            var label = new TextBox { Text = track.Label, ToolTip = "Name of this track in saved clips" };
            AutomationProperties.SetName(label, $"Name of {FlowName(track.Flow)} track {i + 1}");
            label.LostKeyboardFocus += (_, _) => UpdateEndpoint(track, t => t with { Label = label.Text });
            label.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) label.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            };
            fields.Children.Add(label);
            string deviceText = $"{i + 1} · {FlowName(track.Flow)} · " +
                (device != null ? EndpointName(device) : availabilityChecked ? "Missing device (selection kept)" : "Availability not checked");
            fields.Children.Add(new TextBlock
            {
                Text = deviceText,
                ToolTip = deviceText,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)FindResource("Hint"),
            });
            if (AppProfile.IsPersonal && track.Flow == EndpointFlow.Microphone)
                fields.Children.Add(AddMicrophoneMeter(_endpointMeters, () => track.DeviceId, $"microphone {caption}").Panel);
            row.Children.Add(fields);
            EndpointRows.Children.Add(row);
        }

        PopulatePicker(PlaybackPicker, PlaybackPlaceholder, EndpointFlow.Playback, "Choose a playback device");
        PopulatePicker(MicrophonePicker, MicrophonePlaceholder, EndpointFlow.Microphone, "Choose a microphone");
        UpdateEndpointNotice();
        UpdateAddEndpointButton();
        UpdateMicrophoneMonitoring();

        void PopulatePicker(ComboBox picker, TextBlock placeholder, EndpointFlow flow, string prompt)
        {
            var previous = (picker.SelectedItem as Choice)?.Value as AudioEndpoint;
            var assigned = tracks.Select(track => track.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var choices = EndpointAudio.ForFlow(endpoints, flow).Where(endpoint => !assigned.Contains(endpoint.Id))
                .OrderBy(EndpointName, StringComparer.CurrentCultureIgnoreCase)
                .Select(endpoint => new Choice(EndpointName(endpoint) + (endpoint.IsDefault ? " (Windows default)" : ""), endpoint))
                .ToArray();
            picker.ItemsSource = choices;
            picker.SelectedItem = previous == null ? null : choices.FirstOrDefault(choice => choice.Value is AudioEndpoint endpoint &&
                EndpointAudio.SameDevice(endpoint.Flow, endpoint.Id, previous.Flow, previous.Id));
            picker.IsEnabled = tracks.Count < EndpointAudio.MaxTracks && choices.Length != 0;
            placeholder.Text = tracks.Count >= EndpointAudio.MaxTracks ? "Track limit reached" :
                availabilityChecked && choices.Length == 0 ? "No unassigned devices" : prompt;
        }
    }

    private Button EndpointButton(string caption, string accessibleName, bool enabled, Action run)
    {
        var button = new Button { Content = caption, IsEnabled = enabled, Focusable = true, Style = (Style)FindResource("TextButton") };
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
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var picker = sender == AddPlaybackButton ? PlaybackPicker : MicrophonePicker;
        if (settings.EndpointTracks.Count >= EndpointAudio.MaxTracks ||
            picker.SelectedItem is not Choice { Value: AudioEndpoint endpoint } ||
            settings.EndpointTracks.Any(track => StringComparer.OrdinalIgnoreCase.Equals(track.DeviceId, endpoint.Id))) return;
        settings.EndpointTracks = [.. settings.EndpointTracks, new EndpointTrack(endpoint.Flow, endpoint.Id, EndpointName(endpoint))];
        CommitEndpoints(rebuild: true);
    }

    private void MoveEndpoint(int index, int direction)
    {
        var tracks = AppSettings.Current.EndpointTracks.ToList();
        int next = index + direction;
        (tracks[index], tracks[next]) = (tracks[next], tracks[index]);
        AppSettings.Current.EndpointTracks = tracks;
        CommitEndpoints(rebuild: true);
    }

    private void UpdateEndpoint(EndpointTrack selected, Func<EndpointTrack, EndpointTrack> change)
    {
        var tracks = AppSettings.Current.EndpointTracks.ToList();
        int index = tracks.FindIndex(track => EndpointAudio.SameDevice(track.Flow, track.DeviceId, selected.Flow, selected.DeviceId));
        if (index < 0) return;
        var updated = change(tracks[index]);
        if (updated == tracks[index]) return;
        tracks[index] = updated;
        AppSettings.Current.EndpointTracks = tracks;
        CommitEndpoints();
    }

    private void CommitEndpoints(bool rebuild = false)
    {
        AppSettings.Current.Save();
        if (rebuild) ShowEndpointRows();
        else UpdateEndpointNotice();
        UpdateEstimates();
        ScheduleRecorderRestart();
    }

    private void UpdateEndpointNotice()
    {
        if (_endpointError != null) { EndpointNotice.Text = _endpointError; return; }
        var plan = EndpointAudio.Plan(AppSettings.Current);
        if (plan == null) return;
        int missing = _endpointsLoaded ? plan.Count(track => EndpointAudio.FindEndpoint(track, _endpoints) == null) : 0;
        int playback = plan.Count(t => t.Flow == EndpointFlow.Playback);
        var notices = new List<string>();
        if (missing > 0) notices.Add($"{missing} enabled device(s) missing. QuickClip keeps those selections and retries the same devices without substituting a default.");
        if (playback > 1) notices.Add("Multiple playback devices may carry the same audio, such as a headset and a virtual output feeding it.");
        if (playback > 0) notices.Add("Playback loopback includes QuickClip previews played through a selected device.");
        if (_endpointsLoaded && _endpoints.Count == 0 && missing == 0) notices.Add("No active devices found.");
        EndpointNotice.Text = string.Join(" ", notices);
    }

    private async void ReadEndpointStatus_Click(object sender, RoutedEventArgs e)
    {
        int request = ++_statusRequest;
        ReadEndpointStatusButton.IsEnabled = false;
        try
        {
            var lanes = await Task.Run(Recorder.Instance.ReadRunningEndpointAudio);
            if (request != _statusRequest) return;
            EndpointStatusText.Text = lanes.Length == 0 ? "No endpoint audio lanes are running." :
                "Snapshot of the running recorder. Pending Settings edits may differ.\n" +
                string.Join("\n", lanes.Select(DescribeLane));
        }
        catch (Exception ex)
        {
            if (request == _statusRequest) EndpointStatusText.Text = "Could not read running audio status.";
            Log.Write("Reading running endpoint status failed: " + ex.GetType().Name);
        }
        finally
        {
            if (request == _statusRequest) ReadEndpointStatusButton.IsEnabled = true;
        }
    }

    private void EndpointStatus_Collapsed(object sender, RoutedEventArgs e)
    {
        ++_statusRequest;
        ReadEndpointStatusButton.IsEnabled = true;
        EndpointStatusText.Text = "";
    }

    internal static string DescribeLane(RunningEndpointLane lane, int index)
    {
        string heading = $"{index + 1} · {FlowName(lane.Track.Flow)} · {lane.Track.Label}";
        if (lane.Status is not { } status) return $"{heading} · Waiting for status";
        string stage = status.HResult < 0 ? $"{status.Stage} failed (0x{unchecked((uint)status.HResult):X8})" : status.Stage.ToString();
        string format = status.MixRate > 0 ? $" · {status.MixRate:N0} Hz / {status.MixChannels} ch → {status.CaptureRate:N0} Hz" : "";
        string peak = status.LastPacketPeak is { } value ? $" · last packet peak {value:P0}" : "";
        string age = status.MillisecondsSincePacket is { } ms ? $" · last packet {ms:N0} ms ago" : "";
        return $"{heading} · {stage}{format} · {status.PacketCount:N0} packets{age}{peak}";
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
