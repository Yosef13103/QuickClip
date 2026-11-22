using System.Globalization;
using System.Windows.Input;
using QuickClip.Controls;

namespace QuickClip.Mpv;

/// <summary>
/// Preview-only viewport state shared by Player and Editor. Native HWND events take precedence;
/// WPF wheel events are a fallback for HwndHost input routing and fullscreen transitions.
/// </summary>
internal sealed class VideoPreviewZoomController
{
    private readonly VideoHost _video;
    private readonly Func<MpvPlayer?> _player;
    private readonly Func<bool> _canInteract;
    private readonly Func<double>? _fallbackAspect;
    private VideoPreviewZoom.Transform _state;
    private (int X, int Y)? _lastDrag;
    private double _cachedAspect;

    internal bool IsDragging => _lastDrag.HasValue;
    internal VideoPreviewZoom.Transform State => _state;

    internal VideoPreviewZoomController(VideoHost video, Func<MpvPlayer?> player,
        Func<bool> canInteract, Func<double>? fallbackAspect = null)
    {
        _video = video;
        _player = player;
        _canInteract = canInteract;
        _fallbackAspect = fallbackAspect;
        video.VideoCtrlWheel += ZoomAtNativeCursor;
        video.VideoCtrlPanStart = StartPan;
        video.VideoCtrlPanMove = MovePan;
        video.VideoCtrlPanEnd = EndPan;
    }

    internal void HandleWpfWheel(MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !_canInteract()) return;
        var point = e.GetPosition(_video);
        if (point.X < 0 || point.Y < 0 || point.X > _video.ActualWidth ||
            point.Y > _video.ActualHeight) return;
        var (width, height) = _video.NativeClientSize;
        if (_video.ActualWidth <= 0 || _video.ActualHeight <= 0) return;
        ZoomAtNativeCursor((int)Math.Round(point.X / _video.ActualWidth * width),
            (int)Math.Round(point.Y / _video.ActualHeight * height), e.Delta);
        e.Handled = true;
    }

    private void ZoomAtNativeCursor(int x, int y, int delta)
    {
        var player = _player();
        if (player == null || !_canInteract()) return;
        var (width, height) = _video.NativeClientSize;
        double aspect = Aspect(player, width / Math.Max(1, height));
        var next = VideoPreviewZoom.ZoomAt(_state, delta, x, y, width, height, aspect);
        if (next == _state) return;
        _state = next;
        Apply(player);
        player.CommandAsync("show-text", $"Preview zoom {VideoPreviewZoom.Percent(_state.Zoom)}% (Ctrl+drag to pan; Ctrl+0 to reset)", "900");
    }

    private bool StartPan(int x, int y)
    {
        if (_player() == null || !_canInteract()) return false;
        _lastDrag = (x, y);
        return true; // Ctrl+click must never toggle playback or begin an Editor crop drag.
    }

    private void MovePan(int x, int y)
    {
        if (_lastDrag is not { } previous) return;
        _lastDrag = (x, y);
        var player = _player();
        if (player == null || !_canInteract()) return;
        var (width, height) = _video.NativeClientSize;
        double aspect = Aspect(player, width / Math.Max(1, height));
        var next = VideoPreviewZoom.PanBy(_state, x - previous.X, y - previous.Y, width, height, aspect);
        if (next == _state) return;
        _state = next;
        player.SetProperty("video-pan-x", _state.PanX);
        player.SetProperty("video-pan-y", _state.PanY);
    }

    private void EndPan() => _lastDrag = null;

    private double Aspect(MpvPlayer player, double fallback)
    {
        if (_cachedAspect > 0) return _cachedAspect;
        double dw = Dimension(player, "video-out-params/dw");
        double dh = Dimension(player, "video-out-params/dh");
        if (dw <= 0 || dh <= 0)
        {
            dw = Dimension(player, "video-params/dw");
            dh = Dimension(player, "video-params/dh");
        }
        if (dw > 0 && dh > 0) return _cachedAspect = dw / dh;
        double? candidate = _fallbackAspect?.Invoke();
        return _cachedAspect = candidate is > 0 && double.IsFinite(candidate.Value) ? candidate.Value : fallback;
    }

    private static double Dimension(MpvPlayer player, string name)
    {
        if (player.GetDouble(name) is double number && number > 0) return number;
        return double.TryParse(player.GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var parsed) && parsed > 0 ? parsed : 0;
    }

    private void Apply(MpvPlayer player)
    {
        player.SetProperty("video-zoom", _state.Zoom);
        player.SetProperty("video-pan-x", _state.PanX);
        player.SetProperty("video-pan-y", _state.PanY);
    }

    internal void Reset()
    {
        _lastDrag = null;
        _cachedAspect = 0;
        _state = default;
        if (_player() is { } player) Apply(player);
    }
}
