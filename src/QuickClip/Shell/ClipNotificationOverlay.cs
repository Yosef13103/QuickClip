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
    private TimeSpan[] _frameEnds = [];
    private TimeSpan _imageCycle;
    private int _showRevision;
    private Task<ClipOverlayImage.Asset?>? _imageLoad;
    private bool _disposed;
    private Border? _badge;
    private TextBlock? _glyph;
    private DispatcherTimer? _timer;
    private IntPtr _handle;

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
            _imageLoad = path == null ? null : ClipOverlayImage.LoadAssetAsync(path);
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
        // Success is conveyed by the message, not a decorative checkmark.
        _glyph!.Text = success ? (options.Style == OverlayStyle.Badge ? "●" : "") : "!";
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, success ? "SuccessBrush" : "DangerBrush");
        _glyph.FontSize = options.Size * .32;
        var icon = (Grid)_badge!.Parent;
        icon.Width = icon.Height = options.Size * .64;
        _badge.Width = _badge.Height = options.Size * .55;
        bool badge = options.Style == OverlayStyle.Badge;
        icon.Visibility = badge || _image != null || !success ? Visibility.Visible : Visibility.Collapsed;
        _details!.Visibility = badge ? Visibility.Collapsed : Visibility.Visible;
        icon.Margin = badge ? new Thickness(0) : new Thickness(0, 0, 10, 0);
        _badge.CornerRadius = new CornerRadius(options.Size);
        _badge.SetResourceReference(Border.BorderBrushProperty, success ? "SuccessBrush" : "DangerBrush");
        _ring!.BeginAnimation(UIElement.OpacityProperty, null);
        _ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, success ? "SuccessBrush" : "DangerBrush");
        _ring.RenderTransform = new ScaleTransform();
        _ring.Opacity = 0;

        bool imageOnly = (options.Style == OverlayStyle.ImageOnly || options.ImageOnly) && _image != null;
        if (imageOnly)
        {
            _details.Visibility = Visibility.Collapsed;
            icon.Margin = new Thickness(0);
            icon.Width = options.Size * 2;
            icon.Height = options.Size;
            _badge.Width = icon.Width;
            _badge.Height = icon.Height;
            _badge.BorderThickness = new Thickness(0);
            _badge.Background = Brushes.Transparent;
        }
        else
        {
            _badge.BorderThickness = new Thickness(1);
            _badge.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
        }
        _imageView = _image == null ? null : new Image
        {
            Source = _image.First,
            Stretch = options.ImageFit == OverlayImageFit.Cover ? Stretch.UniformToFill : Stretch.Uniform,
            ClipToBounds = true,
        };
        _badge.Child = _imageView ?? (UIElement)_glyph;
        _details!.Children.Clear();
        _details.Children.Add(new TextBlock
        {
            Text = title, Foreground = accent, FontSize = options.Style == OverlayStyle.Detailed ? 16 : 15,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var lines = error != null ? new[] { error } : details?.Lines(options) ?? [];
        var foreground = Application.Current?.TryFindResource("PrimaryTextBrush") as Brush ?? Brushes.White;
        if (options.Style == OverlayStyle.Compact)
        {
            // Compact is actually compact: title + one horizontally readable summary.
            // Full per-field breakdown remains available in Detailed.
            string summary = string.Join("  ·  ", lines);
            if (!string.IsNullOrEmpty(summary))
                _details.Children.Add(new TextBlock
                {
                    Text = summary, Foreground = foreground, FontSize = 12,
                    TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 5, 0, 0),
                });
        }
        else
        {
            foreach (var line in lines)
                _details.Children.Add(new TextBlock
                {
                    Text = line, Foreground = foreground, FontSize = 12,
                    TextWrapping = TextWrapping.WrapWithOverflow, Margin = new Thickness(0, 5, 0, 0),
                });
        }
        // Keep sufficient width at small icon sizes: previous 320*(Size/72) DIP
        // squeezed every word into a character-wide column.
        double width = options.Style == OverlayStyle.Detailed
            ? Math.Clamp(450 * options.Size / 72, 390, 690)
            : Math.Clamp(360 * options.Size / 72, 310, 570);
        if (imageOnly) width = options.Size * 2;
        if (imageOnly)
        {
            // Image-only means just the asset: no banner background, outline or extra padding.
            _card!.Background = Brushes.Transparent;
            _card.BorderThickness = new Thickness(0);
            _card.Padding = new Thickness(0);
        }
        else
        {
            _card!.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
            _card.BorderThickness = new Thickness(1);
            _card.Padding = new Thickness(badge ? options.Size * .12 : 10);
        }
        _card.Measure(new Size(width, double.PositiveInfinity));
        double height = imageOnly ? options.Size : badge ? options.Size : _card.DesiredSize.Height;
        _surface.RenderTransformOrigin = new Point(.5, .5);
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
            if (options.Animation == OverlayAnimation.Slide)
            {
                // Side anchors enter horizontally; top/bottom-center enter vertically.
                // Exact center is normalized to Grow in ClipOverlayOptions.
                _surface.Opacity = options.Opacity;
                Point offset = SlideVector(width, height, options.Anchor);
                var slide = EnterExitMotion(offset.X != 0 ? offset.X : offset.Y, options.Seconds);
                _surface.RenderTransform.BeginAnimation(
                    offset.X != 0 ? TranslateTransform.XProperty : TranslateTransform.YProperty, slide);
            }
            else if (options.Animation == OverlayAnimation.Grow)
            {
                _surface.Opacity = options.Opacity;
                _surface.RenderTransform = new ScaleTransform(1, 1);
                var scale = EnterExitMotion(.65, options.Seconds, visible: 1);
                _surface.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
                _surface.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
            }
            else
            {
                var fade = new DoubleAnimationUsingKeyFrames();
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(.13))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(options.Opacity, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds - .22))));
                fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(options.Seconds))));
                _surface.BeginAnimation(UIElement.OpacityProperty, fade);
            }
            if (options.Animation == OverlayAnimation.Ripple && !imageOnly)
            {
                var pulse = new DoubleAnimation(1, 1.65, TimeSpan.FromSeconds(.65));
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                _ring.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(.95, 0, TimeSpan.FromSeconds(.65)));
            }
        }
        else _surface.Opacity = options.Opacity;
        if (SystemParameters.ClientAreaAnimation && _image?.Frames.Length > 1)
        {
            // GIF/WebM frame playback is independent of the banner entrance effect.
            // Absolute elapsed-time lookup prevents timer catch-up storms and drift on
            // every subsequent loop (especially 29 x 40 ms frames at 100% CPU).
            _frame = 0;
            _frameEnds = new TimeSpan[_image.Frames.Length];
            TimeSpan cumulative = TimeSpan.Zero;
            for (int i = 0; i < _image.Frames.Length; i++)
            {
                cumulative += _image.Frames[i].Delay;
                _frameEnds[i] = cumulative;
            }
            _imageCycle = cumulative;
            _imageClock = System.Diagnostics.Stopwatch.StartNew();
            _frameTimer!.Interval = SafeFrameInterval(_frameEnds[0]);
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
        _badge.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
        var icon = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        icon.Children.Add(_ring);
        icon.Children.Add(_badge);
        _details = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // Explicit flexible text column, rather than DockPanel intrinsic width
        // collapsing wrapped TextBlocks to a near-vertical letter stack.
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(_details, 1);
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
        _card.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        _surface.Children.Add(_card);
        _window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            ShowActivated = false, Topmost = true, Focusable = false, AllowsTransparency = true,
            Background = Brushes.Transparent, Content = _surface, Width = 72, Height = 72,
        };
        _handle = new WindowInteropHelper(_window).EnsureHandle();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            SetWindowDisplayAffinity(_handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, where the capture API supports it
        const int exStyle = -20;
        SetWindowLong(_handle, exStyle, GetWindowLong(_handle, exStyle) | 0x080000A0);
        _timer = new DispatcherTimer(DispatcherPriority.Background);
        _timer.Tick += (_, _) => Hide();
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render);
        _frameTimer.Tick += (_, _) =>
        {
            if (_imageView == null || _image == null || _imageClock == null || _frameEnds.Length == 0) return;
            var (current, untilNext) = FrameAtElapsed(_imageClock.Elapsed, _frameEnds, _imageCycle);
            if (current != _frame)
            {
                _frame = current;
                _imageView.Source = _image.Frames[current].Bitmap;
            }
            _frameTimer.Interval = SafeFrameInterval(untilNext);
        };
    }

    internal static bool MotionEnabled(bool windowsAnimations, OverlayAnimation animation) =>
        windowsAnimations && animation != OverlayAnimation.None;

    internal static Point SlideVector(double cardWidth, double cardHeight, OverlayAnchor anchor)
    {
        int column = (int)anchor % 3, row = (int)anchor / 3;
        if (column == 0) return new(-cardWidth - 24, 0);
        if (column == 2) return new(cardWidth + 24, 0);
        return row switch
        {
            0 => new(0, -cardHeight - 24),
            2 => new(0, cardHeight + 24),
            _ => new(0, 0), // center has no edge; Normalize() selects Grow.
        };
    }

    internal static double SlideOffset(double cardWidth, OverlayAnchor anchor) =>
        SlideVector(cardWidth, cardWidth, anchor).X;

    private static DoubleAnimationUsingKeyFrames EnterExitMotion(double start, double seconds, double visible = 0)
    {
        double enterEnd = Math.Min(.24, seconds / 3);
        double exitStart = Math.Max(enterEnd, seconds - .24);
        var motion = new DoubleAnimationUsingKeyFrames();
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(visible, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(enterEnd)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        motion.KeyFrames.Add(new DiscreteDoubleKeyFrame(visible, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(exitStart))));
        motion.KeyFrames.Add(new EasingDoubleKeyFrame(start, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)),
            new CubicEase { EasingMode = EasingMode.EaseIn }));
        return motion;
    }

    // Pure, clock-based frame scheduling. No accumulation from previous dispatcher ticks.
    internal static (int Frame, TimeSpan UntilNext) FrameAtElapsed(TimeSpan elapsed,
        IReadOnlyList<TimeSpan> cumulativeEnds, TimeSpan cycle)
    {
        if (cumulativeEnds.Count == 0 || cycle.Ticks <= 0)
            return (0, TimeSpan.FromMilliseconds(100));
        long position = Math.Max(0, elapsed.Ticks) % cycle.Ticks;
        for (int i = 0; i < cumulativeEnds.Count; i++)
            if (position < cumulativeEnds[i].Ticks)
                return (i, TimeSpan.FromTicks(cumulativeEnds[i].Ticks - position));
        return (0, cumulativeEnds[0]);
    }

    private static TimeSpan SafeFrameInterval(TimeSpan due) =>
        TimeSpan.FromMilliseconds(Math.Max(16, Math.Ceiling(due.TotalMilliseconds)));

    internal void Hide()
    {
        _showRevision++;
        _timer?.Stop();
        _frameTimer?.Stop();
        _imageClock = null;
        _frameEnds = [];
        _imageCycle = TimeSpan.Zero;
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
