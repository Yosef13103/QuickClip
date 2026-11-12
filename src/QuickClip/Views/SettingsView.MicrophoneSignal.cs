using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Views;

public partial class SettingsView
{
    private sealed class MicrophoneMeter(FrameworkElement panel, Func<string> id, string caption, ProgressBar level, TextBlock status,
        Button test, Button save)
    {
        public FrameworkElement Panel { get; } = panel;
        public Func<string> Id { get; } = id;
        public string Caption { get; } = caption;
        public ProgressBar Level { get; } = level;
        public TextBlock Status { get; } = status;
        public Button Test { get; } = test;
        public Button Save { get; } = save;
        public NativeAudioSignal? TestResult { get; set; }
        public NativeAudioSignal? Snapshot { get; set; }
        public DateTimeOffset SnapshotUtc { get; set; }
        public string? TestMessage { get; set; }
    }

    private readonly List<MicrophoneMeter> _endpointMeters = [], _legacyMeters = [];
    private readonly NativeAudioSignal[] _signalBuffer = new NativeAudioSignal[EndpointAudio.MaxTracks];
    private DispatcherTimer? _microphoneTimer;
    private MicrophoneMeter? _testingMeter;
    private long _microphoneToken;

    private void InitializeMicrophoneMeters()
    {
        if (!AppProfile.IsPersonal) return;
        _microphoneTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(83) };
        _microphoneTimer.Tick += (_, _) => RefreshMicrophoneMeters();
        AudioCard.IsVisibleChanged += (_, _) => UpdateMicrophoneMonitoring();
        EndpointPanel.IsVisibleChanged += (_, _) => UpdateMicrophoneMonitoring();
        LegacyAudioPanel.IsVisibleChanged += (_, _) => UpdateMicrophoneMonitoring();
        Unloaded += (_, _) => StopMicrophoneMonitoring();
    }

    private void UpdateMicrophoneMonitoring()
    {
        if (_microphoneTimer == null) return;
        if (_testingMeter is { Panel.IsVisible: false }) StopMicrophoneTest("Test stopped.");
        if (AudioCard.IsVisible && _endpointMeters.Concat(_legacyMeters).Any(row => row.Panel.IsVisible))
            _microphoneTimer.Start();
        else
            StopMicrophoneMonitoring();
    }

    private void StopMicrophoneMonitoring()
    {
        _microphoneTimer?.Stop();
        StopMicrophoneTest("Test stopped.");
    }

    private void ClearMicrophoneMeters(List<MicrophoneMeter> meters)
    {
        if (_testingMeter != null && meters.Contains(_testingMeter)) StopMicrophoneTest();
        meters.Clear();
    }

    private MicrophoneMeter AddMicrophoneMeter(List<MicrophoneMeter> meters, Func<string> id, string caption)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 1) };
        var actions = new WrapPanel();
        var level = new ProgressBar { Height = 5, Maximum = 1, Focusable = false, Margin = new Thickness(0, 3, 0, 4) };
        level.SetResourceReference(Control.ForegroundProperty, "AccentFillColorDefaultBrush");
        AutomationProperties.SetName(level, $"{caption} activity");
        var status = new TextBlock { Text = "Ready to test", Style = (Style)FindResource("Hint"), TextWrapping = TextWrapping.Wrap };
        MicrophoneMeter row = null!;
        var test = EndpointButton("Test microphone", $"Test {caption}", true, () => ToggleMicrophoneTest(row));
        var save = EndpointButton("Save diagnostics", $"Save diagnostics for {caption}", false, () => SaveMicrophoneDiagnostics(row));
        row = new(panel, id, caption, level, status, test, save);
        actions.Children.Add(test);
        actions.Children.Add(save);
        panel.Children.Add(level);
        panel.Children.Add(status);
        panel.Children.Add(actions);
        meters.Add(row);
        return row;
    }

    private void ToggleMicrophoneTest(MicrophoneMeter row)
    {
        if (row == _testingMeter)
        {
            StopMicrophoneTest("Test stopped.");
            RefreshMicrophoneMeters();
            return;
        }
        row.TestResult = null;
        row.Snapshot = null;
        row.TestMessage = null;
        if (!IsAvailable)
        {
            row.TestMessage = "The recording engine is unavailable.";
            RefreshMicrophoneMeters();
            return;
        }
        try
        {
            int result = StartMicrophoneTest(row.Id(), out long token);
            if (result >= 0)
            {
                _testingMeter = row;
                _microphoneToken = token;
            }
            else
                row.TestMessage = result == unchecked((int)0x800700AA)
                    ? "The recorder or another microphone test is active. Wait until it stops to run this test."
                    : $"Could not start the microphone test (0x{unchecked((uint)result):X8}).";
        }
        catch (EntryPointNotFoundException) { row.TestMessage = "Update the native recording engine to test microphones."; }
        RefreshMicrophoneMeters();
    }

    private void StopMicrophoneTest(string? message = null)
    {
        if (_testingMeter is not { } row) return;
        CaptureEngine.StopMicrophoneTest(_microphoneToken);
        _testingMeter = null;
        _microphoneToken = 0;
        row.TestMessage = message;
    }

    private void RefreshMicrophoneMeters()
    {
        int count = 0;
        try
        {
            if (IsAvailable) count = ReadAudioSignals(_signalBuffer);
        }
        catch (EntryPointNotFoundException)
        {
            StopMicrophoneMonitoring();
            return;
        }

        if (_testingMeter is { } testing)
        {
            if (ReadMicrophoneTest(_microphoneToken) is { } result)
            {
                testing.TestResult = result;
                testing.SnapshotUtc = DateTimeOffset.UtcNow;
                if (result.Stage == EndpointStage.Complete || result.HResult < 0) StopMicrophoneTest();
            }
            else
                StopMicrophoneTest("Microphone test ended when recording started.");
        }

        foreach (var row in _endpointMeters.Concat(_legacyMeters))
        {
            if (!row.Panel.IsVisible) continue;
            string id = row.Id();
            NativeAudioSignal? running = null;
            for (int i = 0; i < count; i++)
            {
                if (!MicrophoneSignal.Matches(_signalBuffer[i], id)) continue;
                running = _signalBuffer[i];
                break;
            }
            var test = row.TestResult is { } sample && MicrophoneSignal.Matches(sample, id) ? sample : (NativeAudioSignal?)null;
            var snapshot = running ?? test;
            bool testingNow = row == _testingMeter;
            if (running.HasValue) row.SnapshotUtc = DateTimeOffset.UtcNow;
            row.Snapshot = snapshot;
            row.Save.IsEnabled = snapshot.HasValue;
            row.Test.IsEnabled = testingNow || (_testingMeter == null && running == null);
            string testCaption = testingNow ? "Stop test" : "Test microphone";
            if (!Equals(row.Test.Content, testCaption))
            {
                row.Test.Content = testCaption;
                AutomationProperties.SetName(row.Test, testingNow ? $"Stop test for {row.Caption}" : $"Test {row.Caption}");
            }
            double target = snapshot is { Stage: EndpointStage.Streaming, HResult: >= 0 } live && (running.HasValue || testingNow)
                ? MicrophoneSignal.MeterValue(live.RecentPeak) : 0;
            row.Level.Value = target >= row.Level.Value ? target : Math.Max(target, row.Level.Value - 0.12);
            string text = running is { } active ? MicrophoneSignal.Describe(active) :
                row.TestMessage ?? (test is { } idle ? MicrophoneSignal.Describe(idle) : "Ready to test");
            if (testingNow) text = "Testing · " + text;
            if (row.Status.Text != text) row.Status.Text = text;
        }
    }

    private void SaveMicrophoneDiagnostics(MicrophoneMeter row)
    {
        if (row.Snapshot is not { } snapshot) return;
        var snapshotUtc = row.SnapshotUtc;
        var dialog = new SaveFileDialog
        {
            Title = "Save local microphone diagnostics",
            FileName = $"quickclip-microphone-{snapshotUtc:yyyyMMdd-HHmmss}.json",
            Filter = "JSON files (*.json)|*.json",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var options = new JsonSerializerOptions { IncludeFields = true, WriteIndented = true };
            options.Converters.Add(new JsonStringEnumConverter());
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { CapturedUtc = snapshotUtc, Signal = snapshot }, options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), "Could not save the diagnostic file.", "QuickClip",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
