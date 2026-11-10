using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickClip.Recording;

namespace QuickClip.Views;

public partial class SettingsView
{
    private IReadOnlyList<AudioEndpoint> _endpoints = [];
    private bool _loadingEndpoints;
    private string? _endpointError;

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
        }
        ShowEndpointRows();
    }

    private static string EndpointName(AudioEndpoint endpoint) =>
        string.IsNullOrWhiteSpace(endpoint.Name) ? FlowName(endpoint.Flow) : endpoint.Name;

    private static string FlowName(EndpointFlow flow) => flow == EndpointFlow.Playback ? "Playback" : "Microphone";

    private void ShowEndpointRows()
    {
        var tracks = AppSettings.Current.EndpointTracks;
        var devices = _endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        EndpointRows.Children.Clear();
        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            int index = i;
            var device = devices.GetValueOrDefault(track.DeviceId);
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(EndpointButton("Up", index > 0, () => MoveEndpoint(index, -1)));
            actions.Children.Add(EndpointButton("Down", index + 1 < tracks.Count, () => MoveEndpoint(index, 1)));
            actions.Children.Add(EndpointButton("Remove", true, () =>
            {
                var remaining = AppSettings.Current.EndpointTracks.ToList();
                remaining.RemoveAt(index);
                AppSettings.Current.EndpointTracks = remaining;
                CommitEndpoints(rebuild: true);
            }));
            row.Children.Add(actions);

            var enabled = new CheckBox { IsChecked = track.Enabled, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Record this device" };
            enabled.Checked += (_, _) => UpdateEndpoint(track.DeviceId, t => t with { Enabled = true });
            enabled.Unchecked += (_, _) => UpdateEndpoint(track.DeviceId, t => t with { Enabled = false });
            row.Children.Add(enabled);

            var fields = new StackPanel { Margin = new Thickness(8, 0, 4, 0) };
            var label = new TextBox { Text = track.Label, ToolTip = "Name of this track in saved clips" };
            label.LostKeyboardFocus += (_, _) => UpdateEndpoint(track.DeviceId, t => t with { Label = label.Text });
            label.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) label.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            };
            fields.Children.Add(label);
            fields.Children.Add(new TextBlock
            {
                Text = device != null ? $"{FlowName(track.Flow)} · {EndpointName(device)}" : $"{FlowName(track.Flow)} · unavailable",
                TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)FindResource("Hint"),
            });
            row.Children.Add(fields);
            EndpointRows.Children.Add(row);
        }

        var assigned = tracks.Select(t => t.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        EndpointPicker.ItemsSource = _endpoints.Where(e => !assigned.Contains(e.Id))
            .OrderBy(e => e.Flow).ThenBy(EndpointName)
            .Select(e => new Choice($"{FlowName(e.Flow)} · {EndpointName(e)}{(e.IsDefault ? " (default)" : "")}", e)).ToList();
        EndpointPicker.SelectedIndex = EndpointPicker.Items.Count > 0 ? 0 : -1;
        EndpointPicker.IsEnabled = tracks.Count < EndpointAudio.MaxTracks && EndpointPicker.Items.Count > 0;
        UpdateEndpointNotice();
        UpdateAddEndpointButton();
    }

    private Button EndpointButton(string caption, bool enabled, Action run)
    {
        var button = new Button { Content = caption, IsEnabled = enabled, Style = (Style)FindResource("TextButton") };
        button.Click += (_, _) => run();
        return button;
    }

    private void EndpointPicker_Changed(object sender, SelectionChangedEventArgs e) => UpdateAddEndpointButton();

    private void UpdateAddEndpointButton()
    {
        if (AddEndpointButton != null)
            AddEndpointButton.IsEnabled = AppSettings.Current.EndpointTracks.Count < EndpointAudio.MaxTracks &&
                EndpointPicker.SelectedItem is Choice;
    }

    private void AddEndpoint_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        if (settings.EndpointTracks.Count >= EndpointAudio.MaxTracks ||
            EndpointPicker.SelectedItem is not Choice { Value: AudioEndpoint endpoint }) return;
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

    private void UpdateEndpoint(string id, Func<EndpointTrack, EndpointTrack> change)
    {
        var tracks = AppSettings.Current.EndpointTracks.ToList();
        int index = tracks.FindIndex(t => string.Equals(t.DeviceId, id, StringComparison.OrdinalIgnoreCase));
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
        var available = _endpoints.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int missing = plan.Count(t => !available.Contains(t.DeviceId));
        int playback = plan.Count(t => t.Flow == EndpointFlow.Playback);
        var notices = new List<string>();
        if (missing > 0) notices.Add($"{missing} selected device(s) unavailable. Their tracks stay silent until they return.");
        if (playback > 1) notices.Add("Multiple playback devices may carry the same audio, such as a headset and a virtual output feeding it.");
        if (playback > 0) notices.Add("Playback loopback includes QuickClip previews played through a selected device.");
        if (_endpoints.Count == 0 && missing == 0) notices.Add("No active devices found.");
        EndpointNotice.Text = string.Join(" ", notices);
    }
}
