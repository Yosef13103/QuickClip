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
    private StackPanel? _details;
    private Border? _card;
    private string? _imagePath;
    private ClipOverlayImage.Asset? _image;
    private Image? _imageView;
    private DispatcherTimer? _frameTimer;
    private int _frame;
    private System.Diagnostics.Stopwatch? _imageClock;
    private TimeSpan _nextFrame;
    private TimeSpan _imageCycle;
    private int _showRevision;
    private Task<ClipOverlayImage.Asset?>? _imageLoad;
    private bool _disposed;
    private Border? _badge;
    private TextBlock? _glyph;
    private DispatcherTimer? _timer;
    private IntPtr _handle;
    private HwndSource? _source;

    internal bool IsVisible => _window?.IsVisible == true;
    internal bool IsAnimatingImage => _frameTimer?.IsEnabled == true;

    internal async Task ShowAsync(ClipOverlayOptions options, string title, bool success, ClipNotificationDetails? details = null, string? error = null)
    {
        if (_disposed) return;
        Hide();
        int revision = _showRevision;
        options = options.Normalize();
        if (!string.Equals(_imagePath, options.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            _imagePath = options.ImagePath;
            _image = null;
            string? path = options.ImagePath;
            _imageLoad = path == null ? null : Task.Run(() => ClipOverlayImage.LoadAsset(path));
        }
        if (_imageLoad != null)
        {
            var asset = await _imageLoad;
            if (revision != _showRevision || _disposed) return;
            _image = asset;
            _imageLoad = null;
            if (asset == null) _imagePath = null; // Retry an unavailable file on a later notification.
        }
        var displays = Displays();
        var display = SelectDisplay(displays, options.Display);
        if (display == null) return;
        EnsureWindow();
        _timer!.Stop();
        _surface!.BeginAnimation(UIElement.OpacityProperty, null);
        _surface.Opacity = 0;
        var accent = Application.Current?.TryFindResource(success ? "SuccessBrush" : "DangerBrush") as Brush
            ?? new SolidColorBrush(success ? Color.FromRgb(92, 237, 172) : Color.FromRgb(255, 127, 131));
        _glyph!.Text = success ? "✓" : "!";
        _glyph.Foreground = accent;
        _glyph.FontSize = options.Size * .32;
        var icon = (Grid)_badge!.Parent;
        icon.Width = icon.Height = options.Size * .64;
        _badge.Width = _badge.Height = options.Size * .55;
        bool badge = options.Style == OverlayStyle.Badge;
        _details!.Visibility = badge ? Visibility.Collapsed : Visibility.Visible;
        icon.Margin = badge ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        _badge.CornerRadius = new CornerRadius(options.Size);
        _badge.BorderBrush = accent;
        _ring!.BeginAnimation(UIElement.OpacityProperty, null);
        _ring.Stroke = accent;
        _ring.RenderTransform = new ScaleTransform();
        _ring.Opacity = 0;

        bool imageOnly = options.ImageOnly && _image != null;
        if (imageOnly)
        {
            _details.Visibility = Visibility.Collapsed;
            icon.Margin = new Thickness(0);
            icon.Width = options.Size * 2;
            icon.Height = options.Size;
            _badge.Width = icon.Width;
            _badge.Height = icon.Height;
            _badge.BorderThickness = new Thickness(0);
        }
        else _badge.BorderThickness = new Thickness(1);
        _imageView = _image == null ? null : new Image { Source = _image.First, Stretch = Stretch.Uniform };
        _badge.Child = _imageView ?? (UIElement)_glyph;
        _details!.Children.Clear();
        _details.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = accent, FontSize = 14, FontWeight = FontWeights.SemiBold,
        });
        var lines = error != null ? new[] { error } : details?.Lines(options) ?? [];
        if (options.Style == OverlayStyle.Compact && lines.Length > 1)
            lines = [string.Join(" · ", lines)];
        foreach (var line in lines)
        {
            _details.Children.Add(new TextBlock
            {
                Text = line, Foreground = Brushes.White, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 0),
            });
        }
        double width = badge ? options.Size : (options.Style == OverlayStyle.Detailed ? 390 : 320) * options.Size / 72;
        if (imageOnly) width = options.Size * 2 + 12;
        _card!.Padding = new Thickness(badge ? options.Size * .12 : 10);
        _card.Measure(new Size(width, double.PositiveInfinity));
        double height = imageOnly ? options.Size + 20 : badge ? options.Size : _card.DesiredSize.Height;
        _surface.RenderTransform = new TranslateTransform();
        _window!.Width = width;
        _window.Height = height;
        if (!_window.IsVisible) _window.Show();
        var prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            GetDpiForMonitor(display.Handle, 0, out uint dpi, out _);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            var pixels = new Size(Math.Round(width * scale), Math.Round(height * scale));
            Point position = ClipOverlayOptions.Place(display.Bounds, pixels, 18 * scale, options.Anchor);
            SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(position.X), (int)Math.Round(position.Y),
                (int)pixels.Width, (int)pixels.Height, 0x0210);
        }
        finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            SetWindowDisplayAffinity(_handle, 0x11);
        if (MotionEnabled(SystemParameters.ClientAreaAnimation, options.Animation))
        {
            var fade = new DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.13))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds - .22))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds))));
            _surface.BeginAnimation(UIElement.OpacityProperty, fade);
            if (options.Animation == OverlayAnimation.Slide)
            {
                int column = (int)options.Anchor % 3;
                var slide = _surface.RenderTransform;
                var duration = TimeSpan.FromSeconds(.16);
                slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation((column - 1) * 12, 0, duration));
            }
            if (options.Animation == OverlayAnimation.Ripple)
            {
                var pulse = new DoubleAnimation(.64, 1, TimeSpan.FromSeconds(.55));
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                _ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.85, 0, TimeSpan.FromSeconds(.55)));
            }
        }
        else _surface.Opacity = options.Opacity;
        if (SystemParameters.ClientAreaAnimation && options.Animation != OverlayAnimation.None && _image?.Frames.Length > 1)
        {
            _frame = 0;
            _imageClock = System.Diagnostics.Stopwatch.StartNew();
            _nextFrame = _image.Frames[0].Delay;
            _imageCycle = TimeSpan.FromTicks(_image.Frames.Sum(frame => frame.Delay.Ticks));
            _frameTimer!.Interval = _nextFrame;
            _frameTimer.Start();
        }
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
        var icon = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        icon.Children.Add(_ring);
        icon.Children.Add(_badge);
        _details = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var content = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        content.Children.Add(icon);
        content.Children.Add(_details);
        _card = new Border
        {
            Background = Application.Current?.TryFindResource("OverlayBackgroundBrush") as Brush
                ?? new SolidColorBrush(Color.FromArgb(245, 23, 28, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 70, 75)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12), Child = content,
        };
        _surface.Children.Add(_card);
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Focusable = false, AllowsTransparency = true,
            Background = Brushes.Transparent, Content = _surface, Width = 72, Height = 72,
        };
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(OnWindowMessage);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            SetWindowDisplayAffinity(_handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, where the capture API supports it
        const int exStyle = -20;
        SetWindowLong(_handle, exStyle, GetWindowLong(_handle, exStyle) | 0x080000A0);
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) => Hide();
        _frameTimer = new DispatcherTimer(DispatcherPriority.Background);
        _frameTimer.Tick += (_, _) =>
        {
            if (_imageView == null || _image == null || _imageClock == null) return;
            var elapsed = _imageClock.Elapsed;
            if (elapsed >= _nextFrame + _imageCycle)
                _nextFrame += TimeSpan.FromTicks((elapsed - _nextFrame).Ticks / _imageCycle.Ticks * _imageCycle.Ticks);
            while (elapsed >= _nextFrame)
            {
                _frame = (_frame + 1) % _image.Frames.Length;
                _nextFrame += _image.Frames[_frame].Delay;
            }
            _imageView.Source = _image.Frames[_frame].Bitmap;
            _frameTimer.Interval = _nextFrame - elapsed;
        };
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x007E && _window?.IsVisible == true) // WM_DISPLAYCHANGE
            _window.Dispatcher.BeginInvoke(Hide);
        return IntPtr.Zero;
    }

    internal static bool MotionEnabled(bool windowsAnimations, OverlayAnimation animation) =>
        windowsAnimations && animation != OverlayAnimation.None;

    internal void Hide()
    {
        _showRevision++;
        _timer?.Stop();
        _frameTimer?.Stop();
        _imageClock = null;
        _imageView = null;
        _window?.Hide();
        _surface?.BeginAnimation(UIElement.OpacityProperty, null);
        if (_surface != null) _surface.RenderTransform = null;
        if (_ring != null)
        {
            _ring.BeginAnimation(UIElement.OpacityProperty, null);
            _ring.RenderTransform = null;
        }
        _details?.Children.Clear();
        if (_badge != null) _badge.Child = _glyph;
    }

    public void Dispose()
    {
        _disposed = true;
        Hide();
        _image = null;
        _imageLoad = null;
        _source?.RemoveHook(OnWindowMessage);
        _source = null;
        _window?.Close();
        _window = null;
    }

    internal static Display? SelectDisplay(IEnumerable<Display> displays, string? preferred)
    {
        var available = displays as IReadOnlyList<Display> ?? displays.ToArray();
        return available.FirstOrDefault(d => string.Equals(d.Device, preferred, StringComparison.OrdinalIgnoreCase))
            ?? available.FirstOrDefault(d => d.Primary) ?? available.FirstOrDefault();
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
