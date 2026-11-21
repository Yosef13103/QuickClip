using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickClip.Recording;
using QuickClip.Shell;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class RecordingSettingsViewTests
{
    [Fact]
    public async Task Audio_and_hotkey_sections_expose_accessible_controls_and_keep_missing_inputs()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new SettingsView();
                AudioEndpoint render = new(EndpointFlow.Playback, "output-fixture", "Same name", true);
                AudioEndpoint mic = new(EndpointFlow.Microphone, "input-fixture", "Same name", true);
                view.RenderEndpointRows([], [render, mic], true);
                Assert.Single(view.PlaybackPicker.Items);
                Assert.Single(view.MicrophonePicker.Items);
                Assert.Equal("Playback device", AutomationProperties.GetName(view.PlaybackPicker));
                Assert.Equal("Microphone input device", AutomationProperties.GetName(view.MicrophonePicker));
                Assert.NotNull(view.PlaybackPicker.SelectedItem);
                Assert.NotNull(view.MicrophonePicker.SelectedItem);
                Assert.True(view.AddPlaybackButton.IsEnabled);
                Assert.True(view.AddMicrophoneButton.IsEnabled);
                var alternate = new AudioEndpoint(EndpointFlow.Playback, "another-output", "AAA", false);
                view.RenderEndpointRows([], [alternate, render, mic], true);
                Assert.Equal(0, view.PlaybackPicker.SelectedIndex);
                view.PlaybackPicker.SelectedIndex = 1;
                view.RenderEndpointRows([], [alternate, render, mic], true);
                Assert.Equal(1, view.PlaybackPicker.SelectedIndex);
                view.RenderEndpointRows([new(EndpointFlow.Playback, render.Id.ToUpperInvariant(), "Default output", false)], [render, alternate, mic], true);
                Assert.Single(view.PlaybackPicker.Items);
                view.RenderEndpointRows([new(EndpointFlow.Playback, render.Id, "Default output")], [render, mic], true);
                Assert.Empty(view.PlaybackPicker.Items);
                Assert.False(view.AddPlaybackButton.IsEnabled);

                EndpointTrack saved = new(EndpointFlow.Microphone, mic.Id, "Saved mic", true, 65);
                view.RenderEndpointRows([saved], [render], true);
                Assert.Single(view.PlaybackPicker.Items);
                Assert.Empty(view.MicrophonePicker.Items);
                Assert.Equal("Microphones", Assert.IsType<TextBlock>(view.EndpointRows.Children[0]).Text);
                var tile = Assert.IsType<Border>(view.EndpointRows.Children[1]);
                var body = Assert.IsType<StackPanel>(tile.Child);
                Assert.InRange(tile.Padding.Top, 0, 10);
                var header = Assert.IsType<DockPanel>(body.Children[0]);
                var actions = Assert.IsType<StackPanel>(header.Children[0]);
                var enabled = Assert.IsType<CheckBox>(header.Children[1]);
                Assert.Equal("Record Microphone track Saved mic", AutomationProperties.GetName(enabled));
                var identity = Assert.IsType<StackPanel>(header.Children[2]);
                var nameRow = Assert.IsType<Grid>(identity.Children[0]);
                Assert.Equal(saved.Label, Assert.IsType<TextBlock>(nameRow.Children[0]).Text);
                var edit = Assert.IsType<TextBox>(nameRow.Children[1]);
                Assert.Equal(Visibility.Collapsed, edit.Visibility);
                var rename = Assert.IsType<Button>(nameRow.Children[2]);
                rename.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(saved.Label, edit.Text);
                Assert.Equal(Visibility.Visible, edit.Visibility);
                Assert.Contains("Device disconnected", Assert.IsType<TextBlock>(identity.Children[1]).Text);
                var volume = Assert.IsType<Slider>(Assert.IsType<Grid>(body.Children[1])
                    .Children.OfType<Slider>().Single());
                Assert.Equal(65, volume.Value);
                Assert.Contains("Saved mic", AutomationProperties.GetName(volume));
                // No 'Manage...' expander. Actions sit beside the name, and one test
                // runs a five-second record+play microphone test.
                Assert.Empty(body.Children.OfType<Expander>());
                var iconActions = actions.Children.OfType<Button>().ToArray();
                Assert.Equal(AppProfile.IsPersonal ? 4 : 3, iconActions.Length);
                Assert.All(iconActions.TakeLast(3), button =>
                {
                    Assert.Same(view.FindResource("AudioSourceIconButton"), button.Style);
                    Assert.NotEmpty(AutomationProperties.GetName(button));
                });
                if (AppProfile.IsPersonal)
                {
                    Assert.Equal("Test mic", iconActions[0].Content);
                    var meter = Assert.IsType<StackPanel>(body.Children[2]);
                    Assert.Equal("microphone Saved mic activity", AutomationProperties.GetName(meter.Children[0]));
                    Assert.Equal(2, meter.Children.Count); // signal + status only
                }
                Assert.Empty(view.EndpointPanel.Children.OfType<Expander>());
                view.RenderEndpointRows(
                    [new(EndpointFlow.Microphone, mic.Id, "Mic", true),
                     new(EndpointFlow.Playback, render.Id, "Game", true),
                     new(EndpointFlow.Playback, alternate.Id, "Media", false)],
                    [mic, render, alternate], true);
                Assert.Equal("Playback", Assert.IsType<TextBlock>(view.EndpointRows.Children[0]).Text);
                Assert.Equal("Microphones", Assert.IsType<TextBlock>(view.EndpointRows.Children[3]).Text);
                var disabled = view.EndpointRows.Children.OfType<Border>().FirstOrDefault(b => b.Opacity < 1);
                Assert.NotNull(disabled);
                view.HotkeysSection.IsChecked = true;
                var size = new Size(1050, 660);
                view.Measure(size);
                view.Arrange(new Rect(size));
                Assert.True(view.HotkeyRows.ActualHeight > 0);
                Assert.Equal(AppProfile.IsPersonal ? 4 : 1, view.HotkeyRows.Children.Count);
                foreach (StackPanel row in view.HotkeyRows.Children)
                {
                    var buttons = Assert.IsType<WrapPanel>(row.Children[2]);
                    Assert.All(buttons.Children.OfType<Button>(), button =>
                    {
                        Assert.True(button.Focusable);
                        Assert.NotEmpty(AutomationProperties.GetName(button));
                    });
                }
                Assert.Contains("QuickClip", SettingsView.HotkeyMessage(HotkeyRegistration.Duplicate));
                Assert.Contains("Windows", SettingsView.HotkeyMessage(HotkeyRegistration.Unavailable));
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(9.7, "9s")]
    [InlineData(59.999, "59s")]
    [InlineData(60, "01:00")]
    [InlineData(60.6, "01:00")]
    [InlineData(125.25, "02:05")]
    [InlineData(3_600, "60:00")]
    public void Compact_labels_do_not_round_into_the_next_second(double seconds, string expected) =>
        Assert.Equal(expected, Fmt.CompactDuration(seconds));

    [Fact]
    public void Running_lane_status_shows_failure_without_raw_endpoint_identity()
    {
        var lane = new RunningEndpointLane(new(EndpointFlow.Microphone, "private-id", "Voice"),
            new(0, EndpointFlow.Microphone, EndpointStage.Initialize, unchecked((int)0x88890008), 48_000, 2, 48_000, 0, null, null));
        string status = SettingsView.DescribeLane(lane, 0);
        Assert.Contains("Microphone · Voice", status);
        Assert.Contains("Initialize failed (0x88890008)", status);
        Assert.Contains("0 packets", status);
        Assert.DoesNotContain(lane.Track.DeviceId, status);
        Assert.DoesNotContain("peak", status);
    }

    [Fact]
    public void Running_playback_status_shows_format_and_packet_health_without_native_ids()
    {
        var lane = new RunningEndpointLane(new(EndpointFlow.Playback, "private-chat-id", "Chat"),
            CaptureEngine.ToEndpointStatus(new()
            {
                TrackIndex = 1, Flow = EndpointFlow.Playback, Stage = EndpointStage.Streaming,
                MixRate = 44_100, MixChannels = 2, MixTag = 0xFFFE, MixSubtype = 1,
                MixBits = 32, MixValidBits = 24, CaptureRate = 48_000, CaptureChannels = 2,
                PacketCount = 120, SilentPackets = 8, Discontinuities = 3, TimestampFallbacks = 5,
                LastPeak = 0.5f, LastRms = 0.25f, MaxPeak = 1.25f, MaxRms = 0.5f,
                SelectedId = "private-chat-id", ResolvedId = "private-resolved-id", DefaultCaptureId = "private-default-id"
            }));

        string status = SettingsView.DescribeLane(lane, 1);
        Assert.Contains("Playback · Chat", status);
        Assert.Contains("PCM 24/32-bit", status);
        Assert.Contains("float32", status);
        Assert.Contains("120 packets", status);
        Assert.Contains("8 silent", status);
        Assert.Contains("3 discontinuities", status);
        Assert.Contains("5 timestamp fallbacks", status);
        Assert.Contains("maximum packet peak", status);
        Assert.Contains("RMS", status);
        Assert.DoesNotContain("private-", status);
    }
}
