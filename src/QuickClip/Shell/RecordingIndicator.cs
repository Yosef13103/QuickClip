using System.Windows.Threading;
using Microsoft.Win32;
using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Shell;

/// <summary>Optional screen HUD owned by the tray host, not the frontend or the capture graph.</summary>
internal sealed class RecordingIndicator : IDisposable
{
    private readonly ClipNotificationOverlay _overlay = new();
    private readonly MicrophoneIndicatorSignal _signal = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private DispatcherTimer? _timer;
    private NativeAudioSignal[]? _buffer;
    private long _epoch = -1;
    private bool _disposed;
    private string? _failureReported;
    private readonly ClipNotificationOverlay _preview = new();
    private DispatcherTimer? _previewTimer;
    private DispatcherTimer? _showRetry;
    private int _showAttempts;
    internal static bool IsEnabled(AppSettings settings) =>
        settings.ShowGameRecordingIndicator && settings.AllowRecordingIconsInCapture;

    internal string Status => _overlay.IndicatorFailure ?? (_overlay.IsVisible
        ? "Visible on the recorded display. Icons may appear in footage."
        : "Icons appear during replay recording after you enable them.");

    internal RecordingIndicator()
    {
        if (!AppProfile.IsPersonal) return;
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.UserPreferenceChanged += PreferenceChanged;
    }

    private void DisplayChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke((Action)Refresh);
    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => DisplayChanged(sender, e);

    internal void Refresh() => Refresh(Recorder.Instance.Feedback);

    internal void Refresh(ReadyCapture? state)
    {
        if (_disposed) return;
        _previewTimer?.Stop();
        if (_preview.IsVisible) _preview.Hide();
        var settings = AppSettings.Current;
        if (state == null)
        {
            _signal.Reset();
            _epoch = -1;
            Hide();
            return;
        }
        if (_epoch != state.Epoch) _showAttempts = 0;
        _epoch = state.Epoch;
        _signal.Configure(_epoch, state.Microphones, state.StartedAt);
        if (!IsEnabled(settings))
        {
            Hide();
            return;
        }
        var microphone = settings.ShowRecordingMicrophoneIndicator ? _signal.State : MicrophoneIndicatorState.Hidden;
        _overlay.ShowRecordingIndicator(state.Device, settings.GameRecordingIndicatorAnchor, microphone);
        if (_overlay.IndicatorFailure is { } failure && failure != _failureReported)
        {
            _failureReported = failure;
            Log.Event(DiagnosticCategory.Capture, "Recording HUD unavailable", DiagnosticSeverity.Warning);
        }
        else if (_overlay.IsVisible) _failureReported = null;
        if (_overlay.IsVisible)
        {
            _showRetry?.Stop();
            _showAttempts = 0;
        }
        else if (_showAttempts < 3)
        {
            // A transient display/window initialization failure must not require
            // opening settings or previewing the HUD to get another attempt.
            _showRetry ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            if (!_showRetry.IsEnabled)
            {
                _showRetry.Tick -= RetryShow;
                _showRetry.Tick += RetryShow;
                _showRetry.Start();
            }
        }
        if (!_overlay.IsVisible || microphone is MicrophoneIndicatorState.Hidden or MicrophoneIndicatorState.Active)
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

    private void RetryShow(object? sender, EventArgs e)
    {
        _showRetry?.Stop();
        _showAttempts++;
        Refresh();
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
        return "Preview on primary display for four seconds.";
    }

    private void Sample()
    {
        var settings = AppSettings.Current;
        var state = Recorder.Instance.Feedback;
        if (state?.Epoch != _epoch || !IsEnabled(settings) || !settings.ShowRecordingMicrophoneIndicator)
        {
            Refresh(state);
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
            if (Recorder.Instance.Feedback?.Epoch != _epoch)
            {
                // A delayed sample must never update the old capture source.
                Refresh();
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
            _overlay.UpdateRecordingIndicatorMicrophone(MicrophoneIndicatorState.Quiet);
            _timer?.Stop();
            _buffer = null;
        }
    }

    private void Hide()
    {
        _showRetry?.Stop();
        _showAttempts = 0;
        _timer?.Stop();
        _buffer = null;
        if (_overlay.IsVisible) _overlay.Hide();
    }

    public void Dispose()
    {
        _disposed = true;
        if (AppProfile.IsPersonal)
        {
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        }
        Hide();
        _overlay.Dispose();
        _previewTimer?.Stop();
        _preview.Dispose();
    }
}
