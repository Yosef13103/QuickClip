using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace QuickClip.Shell;

/// <summary>One reusable no-activate overlay. It is hidden between save events.</summary>
internal sealed class ClipNotificationOverlay : IDisposable
{
    private Window? _window;
    private Grid? _surface;
    private Ellipse? _ring;
    private Border? _badge;
    private TextBlock? _glyph;
    private DispatcherTimer? _timer;
    private IntPtr _handle;

    internal void Show(ClipOverlayOptions options, bool success)
    {
        options = options.Normalize();
        var displays = Displays();
        var display = displays.FirstOrDefault(d => string.Equals(d.Device, options.Display, StringComparison.OrdinalIgnoreCase))
            ?? displays.FirstOrDefault(d => d.Primary) ?? displays.FirstOrDefault();
        if (display == null) return;
        EnsureWindow();
        _timer!.Stop();
        _surface!.BeginAnimation(UIElement.OpacityProperty, null);
        _surface.Opacity = 0;
        var accent = new SolidColorBrush(success ? Color.FromRgb(92, 237, 172) : Color.FromRgb(255, 127, 131));
        _glyph!.Text = success ? "✓" : "!";
        _glyph.Foreground = accent;
        _glyph.FontSize = options.Size * .32;
        _badge!.Width = _badge.Height = options.Size * .68;
        _badge.CornerRadius = new CornerRadius(options.Size);
        _badge.BorderBrush = accent;
        _ring!.Stroke = accent;
        _ring.RenderTransform = new ScaleTransform();
        _ring.Opacity = 0;

        _window!.Width = _window.Height = options.Size;
        if (!_window.IsVisible) _window.Show();
        var prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            GetDpiForMonitor(display.Handle, 0, out uint dpi, out _);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            int pixels = (int)Math.Round(options.Size * scale);
            Point position = ClipOverlayOptions.Place(display.Bounds, pixels, 18 * scale, options.Anchor);
            SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(position.X), (int)Math.Round(position.Y), pixels, pixels, 0x0210);
        }
        finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            SetWindowDisplayAffinity(_handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, where the capture API supports it

        if (SystemParameters.ClientAreaAnimation)
        {
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.13))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds - .22))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds))));
            _surface.BeginAnimation(UIElement.OpacityProperty, fade);
            if (options.Animation == OverlayAnimation.Ripple)
            {
                var pulse = new DoubleAnimation(.64, 1, TimeSpan.FromSeconds(.55));
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                _ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.85, 0, TimeSpan.FromSeconds(.55)));
            }
        }
        else _surface.Opacity = options.Opacity;
        _timer.Interval = TimeSpan.FromSeconds(options.Seconds);
        _timer.Start();
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        _surface = new Grid { IsHitTestVisible = false };
        _ring = new Ellipse { StrokeThickness = 1.5, Margin = new Thickness(3), RenderTransformOrigin = new Point(.5, .5) };
        _glyph = new TextBlock { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _badge = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(236, 23, 28, 32)),
            BorderThickness = new Thickness(1), Child = _glyph,
        };
        _surface.Children.Add(_ring);
        _surface.Children.Add(_badge);
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Focusable = false, AllowsTransparency = true,
            Background = Brushes.Transparent, Content = _surface, Width = 72, Height = 72,
        };
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        const int exStyle = -20;
        SetWindowLong(_handle, exStyle, GetWindowLong(_handle, exStyle) | 0x080000A0);
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _window.Hide();
        };
    }

    public void Dispose()
    {
        _timer?.Stop();
        _window?.Close();
        _window = null;
    }

    internal sealed record Display(IntPtr Handle, string Device, Rect Bounds, bool Primary)
    {
        public string Label => $"{Device.Replace(@"\\.\", "")} · {Bounds.Width:0} × {Bounds.Height:0}";
    }

    internal static List<Display> Displays()
    {
        var result = new List<Display>();
        var prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (GetMonitorInfo(monitor, ref info))
                    result.Add(new(monitor, info.Device, new Rect(info.Monitor.Left, info.Monitor.Top,
                        info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0));
                return true;
            }, IntPtr.Zero);
        }
        finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
        return result;
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr order, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
}
