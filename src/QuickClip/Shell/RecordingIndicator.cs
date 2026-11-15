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
        var settings = AppSettings.Current;
        if (state == null)
        {
            _signal.Reset();
            _epoch = -1;
            Hide();
            return;
        }
        _epoch = state.Epoch;
        _signal.Configure(_epoch, state.Microphones, state.StartedAt);
        if (!settings.ShowGameRecordingIndicator)
        {
            Hide();
            return;
        }
        var microphone = settings.ShowRecordingMicrophoneIndicator ? _signal.State : MicrophoneIndicatorState.Hidden;
        _overlay.ShowRecordingIndicator(state.Device, settings.GameRecordingIndicatorAnchor, microphone);
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

    private void Sample()
    {
        var settings = AppSettings.Current;
        var state = Recorder.Instance.Feedback;
        if (state?.Epoch != _epoch || !settings.ShowGameRecordingIndicator || !settings.ShowRecordingMicrophoneIndicator)
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
    }
}
