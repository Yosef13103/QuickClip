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
                Assert.Null(view.PlaybackPicker.SelectedItem);
                Assert.Null(view.MicrophonePicker.SelectedItem);

                EndpointTrack saved = new(EndpointFlow.Microphone, mic.Id, "Saved mic");
                view.RenderEndpointRows([saved], [render], true);
                Assert.Single(view.PlaybackPicker.Items);
                Assert.Empty(view.MicrophonePicker.Items);
                var track = Assert.IsType<DockPanel>(Assert.Single(view.EndpointRows.Children));
                var fields = Assert.IsType<StackPanel>(track.Children[2]);
                Assert.Equal(saved.Label, Assert.IsType<TextBox>(fields.Children[0]).Text);
                Assert.Contains("Missing device (selection kept)", Assert.IsType<TextBlock>(fields.Children[1]).Text);
                if (AppProfile.IsPersonal)
                {
                    var meter = Assert.IsType<StackPanel>(fields.Children[2]);
                    Assert.Equal("microphone Saved mic activity", AutomationProperties.GetName(meter.Children[0]));
                    var actions = Assert.IsType<WrapPanel>(meter.Children[2]);
                    Assert.Equal("Test microphone", Assert.IsType<Button>(actions.Children[0]).Content);
                    Assert.False(Assert.IsType<Button>(actions.Children[1]).IsEnabled);
                }

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
    [InlineData(0, "00")]
    [InlineData(9.7, "09")]
    [InlineData(59.999, "59")]
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
}
