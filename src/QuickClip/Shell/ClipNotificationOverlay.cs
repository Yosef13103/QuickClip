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
    private Border? _ring;
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
    private DispatcherTimer? _timer;
    private IntPtr _handle;
    private (string Device, OverlayAnchor Anchor, Rect Bounds, bool HasMicrophone)? _indicatorPlacement;
    private System.Windows.Shapes.Path? _indicatorMicrophoneGlyph;

    internal bool IsVisible => _window?.IsVisible == true;
    internal bool IsAnimatingImage => _frameTimer?.IsEnabled == true;

    internal async Task ShowAsync(ClipOverlayOptions options, string title, bool success, ClipNotificationDetails? details = null, string? error = null, ClipOverlayOptions? missingImageFallback = null)
    {
        if (_disposed) return;
        Hide();
        int revision = _showRevision;
        options = options.Normalize();
        bool imageOnly = options.Style == OverlayStyle.ImageOnly || options.ImageOnly;
        // The renderer never loads custom media for a banner.
        if (!imageOnly) options = options with { ImagePath = null, ImageOnly = false, Style = options.Style == OverlayStyle.Detailed ? OverlayStyle.Detailed : OverlayStyle.Compact, Anchor = options.Anchor == OverlayAnchor.Center ? OverlayAnchor.BottomRight : options.Anchor };
        if (imageOnly && (!string.Equals(_imagePath, options.ImagePath, StringComparison.OrdinalIgnoreCase) || (_image == null && _imageLoad == null)))
        {
            _imagePath = options.ImagePath;
            _image = null;
            string? path = options.ImagePath;
            _imageLoad = ClipOverlayImage.LoadTightOverlayAsync(path);
        }
        if (imageOnly && _imageLoad != null)
        {
            var asset = await _imageLoad;
            if (revision != _showRevision || _disposed) return;
            _image = asset;
            _imageLoad = null;
            if (asset == null) _imagePath = null; // Retry an unavailable file on a later notification.
        }
        if (imageOnly && _image == null)
        {
            if (missingImageFallback == null) return;
            imageOnly = false;
            options = missingImageFallback.Banner();
        }
        var displays = Displays();
        var display = SelectDisplay(displays, options.Display);
        if (display == null) return;
        EnsureWindow();
        GetDpiForMonitor(display.Handle, 0, out uint monitorDpi, out _);
        double monitorScale = monitorDpi == 0 ? 1 : monitorDpi / 96d;
        _surface!.LayoutTransform = Transform.Identity;
        _timer!.Stop();
        _surface!.BeginAnimation(UIElement.OpacityProperty, null);
        _surface.Opacity = 0;
        string accentKey = success ? (title == "Clip saved" ? "SuccessBrush" : "AccentBrush") : "DangerBrush";
        var accent = Application.Current?.TryFindResource(accentKey) as Brush
            ?? new SolidColorBrush(success ? Color.FromRgb(92, 237, 172) : Color.FromRgb(255, 127, 131));
        _ring!.BeginAnimation(UIElement.OpacityProperty, null);
        _ring.BorderBrush = accent;
        _ring.RenderTransform = new ScaleTransform(1, 1);
        _ring.Opacity = 0;
        _card!.ClearValue(Border.BackgroundProperty);
        _card.ClearValue(Border.BorderBrushProperty);
        _card.Padding = new Thickness(0);
        _card.BorderThickness = new Thickness(0);
        _card.Child = null;
        _imageView = null;
        double width, height;
        bool detailed = !imageOnly && options.Style == OverlayStyle.Detailed;
        double factor = options.Size / 72;
        if (imageOnly)
        {
            var imageSize = ImageCanvasSize(_image!.First.PixelWidth, _image.First.PixelHeight, options.Size, options.ImageFit);
            width = imageSize.Width;
            height = imageSize.Height;
            _card.Background = Brushes.Transparent;
            _imageView = new Image { Source = _image!.First,
                Stretch = options.ImageFit == OverlayImageFit.Cover ? Stretch.UniformToFill : Stretch.Uniform,
                ClipToBounds = true };
            _card.Child = _imageView;
        }
        else
        {
            width = Math.Min((detailed ? 386 : 310) * factor, Math.Max(120, (display.Bounds.Width - 8) / monitorScale));
            width = Math.Max(120, width);
            _card.SetResourceReference(Border.BackgroundProperty, "OverlayBackgroundBrush");
            _card.SetResourceReference(Border.BorderBrushProperty, "DividerBrush");
            _card.BorderThickness = new Thickness(1);
            _card.CornerRadius = new CornerRadius(12);
            _card.Padding = new Thickness(14, 11, 14, 12);
            var root = new StackPanel();
            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var status = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            status.SetResourceReference(Border.BackgroundProperty, "ElevatedSurfaceBrush");
            status.Child = new TextBlock { Text = success ? title == "Clip saved" ? "✓" : "●" : "!",
                Foreground = accent, FontWeight = FontWeights.SemiBold, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(status);
            var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(heading, 1);
            header.Children.Add(heading);
            var label = Text(title, "PrimaryTextBrush", 15, true);
            heading.Children.Add(label);
            root.Children.Add(header);
            if (error != null)
            {
                heading.Children.Add(Text(error, "SecondaryTextBrush", 12));
            }
            else if (details != null)
            {
                if (detailed)
                {
                    if (options.ShowTitle && !string.IsNullOrWhiteSpace(details.Title))
                        heading.Children.Add(Text(details.Title, "SecondaryTextBrush", 12));
                    var rows = new (string Label, string? Value)[] {
                        ("Game", options.ShowGame ? details.Game : null),
                        ("Source", options.ShowSource ? details.Source : null),
                        ("Duration", options.ShowDuration ? details.Duration : null),
                        ("Resolution", options.ShowResolution ? details.Resolution : null),
                        ("Frame rate", options.ShowFps ? details.Fps : null),
                        ("Shortcut", options.ShowHotkey ? details.Hotkey : null) };
                    var fields = rows.Where(row => !string.IsNullOrWhiteSpace(row.Value)).ToArray();
                    if (fields.Length > 0)
                    {
                        var divider = new Border { Height = 1, Margin = new Thickness(0, 10, 0, 5) };
                        divider.SetResourceReference(Border.BackgroundProperty, "DividerBrush");
                        root.Children.Add(divider);
                        foreach (var row in fields)
                        {
                            var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
                            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            var value = Text(row.Value!, "PrimaryTextBrush", 12);
                            Grid.SetColumn(value, 1);
                            line.Children.Add(Text(row.Label, "SecondaryTextBrush", 11));
                            line.Children.Add(value);
                            root.Children.Add(line);
                        }
                    }
                }
                else
                {
                    var lines = details.Lines(options);
                    if (lines.Length > 0)
                    {
                        var summary = Text(string.Join("  ·  ", lines), "SecondaryTextBrush", 12);
                        summary.MaxHeight = 48;
                        heading.Children.Add(summary);
                    }
                }
            }
            root.LayoutTransform = new ScaleTransform(factor, factor);
            _card.Child = root;
            _card.Margin = new Thickness(0);
            _card.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Ceiling(root.DesiredSize.Height + _card.Padding.Top + _card.Padding.Bottom + 2);
        }
        // Reserve actual transparent space for the expanding wave, rather than
        // scaling a hidden icon inside a clipped content column.
        bool ripple = MotionEnabled(SystemParameters.ClientAreaAnimation, options.Animation) && options.Animation == OverlayAnimation.Ripple;
        double waveSpace = ripple ? 14 : 0;
        _card.Margin = new Thickness(waveSpace);
        _ring.Margin = new Thickness(waveSpace);
        _ring.CornerRadius = new CornerRadius(imageOnly ? 8 : 12);
        double contentWidth = width, contentHeight = height;
        width += 2 * waveSpace;
        height += 2 * waveSpace;
        // Fit even oversized custom notification sizes on a small or high-DPI monitor.
        double fit = Math.Min(1, Math.Min((display.Bounds.Width - 8) / monitorScale / width,
            (display.Bounds.Height - 8) / monitorScale / height));
        if (fit > 0 && fit < 1)
        {
            _surface.LayoutTransform = new ScaleTransform(fit, fit);
            width *= fit;
            height *= fit;
        }
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
            // Images sit four physical pixels from the edge; banners sit two. The
            // ripple reserve stays inside the window so the wave cannot be cut off.
            Rect bounds = imageOnly ? display.Bounds : BannerBounds(display.Bounds, display.WorkArea ?? display.Bounds, options.Anchor, scale, pixels.Height);
            Point position = ClipOverlayOptions.Place(bounds, pixels, imageOnly ? 4 : 2, options.Anchor);
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
            else if (options.Animation is OverlayAnimation.Grow or OverlayAnimation.SoftPop)
            {
                _surface.Opacity = options.Opacity;
                _surface.RenderTransform = new ScaleTransform(1, 1);
                var scale = EnterExitMotion(options.Animation == OverlayAnimation.SoftPop ? .92 : .65, options.Seconds, visible: 1);
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
                if (options.Animation == OverlayAnimation.Lift)
                    _surface.RenderTransform.BeginAnimation(TranslateTransform.YProperty, EnterExitMotion(10, options.Seconds));
            }
            if (ripple)
            {
                var duration = TimeSpan.FromSeconds(Math.Min(.55, options.Seconds * .8));
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, 1 + 22 / contentWidth, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                _ring.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1, 1 + 22 / contentHeight, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                _ring.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.85, 0, duration));
            }
        }
        else _surface.Opacity = options.Opacity;
        if (imageOnly && SystemParameters.ClientAreaAnimation && _image?.Frames.Length > 1)
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

    private static TextBlock Text(string value, string brush, double size, bool bold = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        text.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return text;
    }

    private void EnsureWindow()
    {
        if (_window != null) return;
        _surface = new Grid { IsHitTestVisible = false };
        _ring = new Border { BorderThickness = new Thickness(1.5), RenderTransformOrigin = new Point(.5, .5), Opacity = 0 };
        _card = new Border { CornerRadius = new CornerRadius(12) };
        _surface.Children.Add(_ring);
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

    internal static Size ImageCanvasSize(int pixelWidth, int pixelHeight, double size, OverlayImageFit fit)
    {
        // Preserve the existing maximum footprint. Contain uses only the space
        // the asset needs; explicit Cover still fills the requested 2:1 area.
        if (fit == OverlayImageFit.Cover) return new Size(size * 2, size);
        double scale = Math.Min(2 * size / Math.Max(1, pixelWidth), size / Math.Max(1, pixelHeight));
        return new Size(Math.Max(1, pixelWidth * scale), Math.Max(1, pixelHeight * scale));
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
        _indicatorPlacement = null;
        _indicatorMicrophoneGlyph = null;
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
        if (_card != null) _card.Child = null;
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

    // Separate status surface: no animation, theme lookup or fallback to another monitor.
    internal void ShowRecordingIndicator(string device, OverlayAnchor anchor, MicrophoneIndicatorState microphone)
    {
        if (_disposed) return;
        var display = Displays().FirstOrDefault(d => string.Equals(d.Device, device, StringComparison.OrdinalIgnoreCase));
        if (display == null || !Enum.IsDefined(anchor) || anchor == OverlayAnchor.Center)
        {
            Hide();
            return;
        }
        bool hasMicrophone = microphone != MicrophoneIndicatorState.Hidden;
        var placement = (display.Device, anchor, display.Bounds, hasMicrophone);
        if (IsVisible && _indicatorPlacement == placement)
        {
            UpdateRecordingIndicatorMicrophone(microphone);
            return;
        }
        Hide();
        EnsureWindow();
        // Capture exclusion is a prerequisite, not proof of exclusion in the actual capture backend.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !SetWindowDisplayAffinity(_handle, 0x11)) return;
        _surface!.LayoutTransform = Transform.Identity;
        _surface.Opacity = 1;
        _card!.Background = Brushes.Transparent;
        _card.Padding = new Thickness(0);
        _card.BorderThickness = new Thickness(0);
        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        var camera = StatusGlyph("M2,5 H5 L6.5,3 H9.5 L11,5 H14 V13 H2 Z M10.5,9 A2.5,2.5 0 1 1 5.5,9 A2.5,2.5 0 1 1 10.5,9", out _);
        if ((int)anchor % 3 == 0 || !hasMicrophone) icons.Children.Add(camera);
        if (hasMicrophone)
        {
            icons.Children.Add(StatusGlyph("M6,3 A2,2 0 0 1 10,3 V8 A2,2 0 0 1 6,8 Z M4,7 V8 A4,4 0 0 0 12,8 V7 M8,12 V14 M5.5,14 H10.5", out var mic));
            _indicatorMicrophoneGlyph = mic;
            if ((int)anchor % 3 != 0) icons.Children.Add(camera);
        }
        _card.Child = icons;
        UpdateRecordingIndicatorMicrophone(microphone);
        _window!.Width = hasMicrophone ? 40 : 20;
        _window.Height = 20;
        var prior = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            GetDpiForMonitor(display.Handle, 0, out uint dpi, out _);
            double scale = dpi == 0 ? 1 : dpi / 96d;
            var size = new Size(Math.Round(_window.Width * scale), Math.Round(_window.Height * scale));
            var bounds = BannerBounds(display.Bounds, display.WorkArea ?? display.Bounds, anchor, scale, size.Height);
            Point p = ClipOverlayOptions.Place(bounds, size, 4, anchor);
            if (!SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(p.X), (int)Math.Round(p.Y), (int)size.Width, (int)size.Height, 0x0210)) return;
            _window.Show();
            if (!SetWindowDisplayAffinity(_handle, 0x11) ||
                !SetWindowPos(_handle, new IntPtr(-1), (int)Math.Round(p.X), (int)Math.Round(p.Y), (int)size.Width, (int)size.Height, 0x0210))
            {
                Hide();
                return;
            }
            _indicatorPlacement = placement;
        }
        finally { if (prior != IntPtr.Zero) SetThreadDpiAwarenessContext(prior); }
    }

    private static Grid StatusGlyph(string geometry, out System.Windows.Shapes.Path foreground)
    {
        var data = Geometry.Parse(geometry);
        data.Freeze();
        var cell = new Grid { Width = 20, Height = 20, Opacity = .72 };
        var canvas = new Grid { Width = 16, Height = 16 };
        cell.Children.Add(canvas);
        canvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = data, Stroke = Brushes.Black, StrokeThickness = 3.2, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        foreground = new System.Windows.Shapes.Path
        {
            Data = data, Stroke = Brushes.White, StrokeThickness = 1.5, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
        canvas.Children.Add(foreground);
        return cell;
    }

    internal void UpdateRecordingIndicatorMicrophone(MicrophoneIndicatorState state)
    {
        if (_indicatorMicrophoneGlyph == null) return;
        _indicatorMicrophoneGlyph.Stroke = state == MicrophoneIndicatorState.Active ? Brushes.White : Brushes.Red;
    }

    internal static Rect BannerBounds(Rect display, Rect work, OverlayAnchor anchor, double scale, double height)
    {
        if ((int)anchor / 3 != 2) return display;
        // Reserve at least a normal taskbar height, including on auto-hide/secondary screens.
        double inset = Math.Max(40 * scale, display.Bottom - work.Bottom);
        inset = Math.Clamp(inset, 0, Math.Max(0, display.Height - height - 4));
        return new Rect(display.X, display.Y, display.Width, display.Height - inset);
    }

    internal sealed record Display(IntPtr Handle, string Device, Rect Bounds, bool Primary, Rect? WorkArea = null)
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
                        info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top), (info.Flags & 1) != 0,
                        new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top)));
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
