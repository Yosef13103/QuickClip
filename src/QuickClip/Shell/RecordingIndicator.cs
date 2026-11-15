using System.Windows.Threading;
using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Shell;

/// <summary>Optional screen HUD owned by the tray host, not the frontend or the capture graph.</summary>
internal sealed class RecordingIndicator : IDisposable
{
    private readonly ClipNotificationOverlay _overlay = new();
    private readonly MicrophoneIndicatorSignal _signal = new();
    private DispatcherTimer? _timer;
    private NativeAudioSignal[]? _buffer;
    private long _epoch = -1;
    private bool _disposed;
    private string? _failureReported;
    private readonly ClipNotificationOverlay _preview = new();
    private DispatcherTimer? _previewTimer;
    internal string Status => _overlay.IndicatorFailure ?? (_overlay.IsVisible
        ? (_overlay.IndicatorOpaque ? "Visible using compact opaque fallback; test saved clips." : "Visible; verify exclusion in a saved clip.")
        : "Icons appear during actual replay recording.");

    private static bool Enabled(AppSettings settings, RecorderState state) =>
        AppProfile.IsPersonal && settings.RecordingEnabled && settings.ShowGameRecordingIndicator &&
        state.Running && state.Mode is (RecordingMode.GameTriggered or RecordingMode.ManualDesktop) &&
        state.CaptureDevice != null;

    internal void Refresh()
    {
        if (_disposed) return;
        _previewTimer?.Stop();
        if (_preview.IsVisible) _preview.Hide();
        var settings = AppSettings.Current;
        var state = Recorder.Instance.State;
        if (!Enabled(settings, state))
        {
            Stop();
            return;
        }
        _epoch = state.CaptureEpoch;
        _signal.Configure(_epoch, MicrophoneIndicatorSignal.SelectedMicrophones(settings), Environment.TickCount64);
        _overlay.ShowRecordingIndicator(state.CaptureDevice!, settings.GameRecordingIndicatorAnchor, _signal.State);
        if (_overlay.IndicatorFailure is { } failure && failure != _failureReported)
        {
            _failureReported = failure;
            Log.Write("Recording HUD unavailable: " + failure);
        }
        else if (_overlay.IsVisible) _failureReported = null;
        if (!_overlay.IsVisible || _signal.Selected.Count == 0 || _signal.State == MicrophoneIndicatorState.Active)
        {
            _timer?.Stop();
            _buffer = null;
            return;
        }
        int capacity = Math.Max(EndpointAudio.MaxTracks, _signal.Selected.Count);
        if (_buffer == null || _buffer.Length < capacity) _buffer = new NativeAudioSignal[capacity];
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => Sample();
        }
        _timer.Start();
    }

    internal string Preview()
    {
        if (_disposed) return "Recording icons are unavailable.";
        if (Recorder.Instance.State.Running) { Refresh(); return Status; }
        var display = ClipNotificationOverlay.Displays().FirstOrDefault(d => d.Primary);
        if (display == null) return "No connected primary display.";
        _preview.ShowRecordingIndicator(display.Device, AppSettings.Current.GameRecordingIndicatorAnchor,
            AppSettings.Current.ShowRecordingMicrophoneIndicator ? MicrophoneIndicatorState.Active : MicrophoneIndicatorState.Hidden);
        if (!_preview.IsVisible) return _preview.IndicatorFailure ?? "Could not show preview.";
        if (_previewTimer == null)
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(4) };
            _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _preview.Hide(); };
        }
        _previewTimer.Stop();
        _previewTimer.Start();
        return _preview.IndicatorOpaque ? "Preview: compact opaque fallback. Test actual capture exclusion." : "Preview on primary display for four seconds.";
    }

    private void Sample()
    {
        var settings = AppSettings.Current;
        var state = Recorder.Instance.State;
        if (!Enabled(settings, state) || state.CaptureEpoch != _epoch || !settings.ShowRecordingMicrophoneIndicator)
        {
            Refresh();
            return;
        }
        if (_buffer == null || !_overlay.IsVisible)
        {
            _timer?.Stop();
            return;
        }
        var previous = _signal.State;
        try
        {
            int count = ReadAudioSignals(_buffer);
            var current = _signal.Observe(_buffer, count, Environment.TickCount64);
            if (Recorder.Instance.State.CaptureEpoch != _epoch)
            {
                // A delayed sample must never update the old capture source.
                Stop();
                return;
            }
            if (current != previous) _overlay.UpdateRecordingIndicatorMicrophone(current);
            if (current == MicrophoneIndicatorState.Active)
            {
                _timer?.Stop();
                _buffer = null;
            }
        }
        catch (EntryPointNotFoundException)
        {
            _signal.Reset();
            _overlay.UpdateRecordingIndicatorMicrophone(MicrophoneIndicatorState.Quiet);
            _timer?.Stop();
        }
    }

    private void Stop()
    {
        _timer?.Stop();
        _buffer = null;
        _signal.Reset();
        _epoch = -1;
        if (_overlay.IsVisible) _overlay.Hide();
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _overlay.Dispose();
        _previewTimer?.Stop();
        _preview.Dispose();
    }
}
