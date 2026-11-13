using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using QuickClip.Gallery;

namespace QuickClip.Controls;

/// <summary>Time ruler with the trim range (in/out handles) and the playhead. Wheel zooms, Shift+wheel pans.</summary>
public sealed class TimelineBar : FrameworkElement
{
    public static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F)));
    public static readonly Brush OutsideShade = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00)));
    public static readonly Brush SelectionFill = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xB9, 0x00)));
    public static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)));
    public static readonly Brush PlayheadBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Brush TickBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)));
    private static readonly Pen TickPen = Freeze(new Pen(TickBrush, 1));
    private static readonly Pen PlayheadPen = Freeze(new Pen(PlayheadBrush, 1.5));
    private static readonly Brush BookmarkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0xCC, 0xFF)));
    private static DependencyProperty ThemeBrush(string name, Brush fallback) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(TimelineBar),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ThemeSurfaceProperty = ThemeBrush("ThemeSurface", BackgroundBrush);
    private static readonly DependencyProperty ThemeTextProperty = ThemeBrush("ThemeText", LabelBrush);
    private static readonly DependencyProperty ThemeGridProperty = ThemeBrush("ThemeGrid", TickBrush);
    private static readonly DependencyProperty ThemeHandleProperty = ThemeBrush("ThemeHandle", HandleBrush);
    private static readonly DependencyProperty ThemePlayheadProperty = ThemeBrush("ThemePlayhead", PlayheadBrush);
    private static readonly DependencyProperty ThemeBookmarkProperty = ThemeBrush("ThemeBookmark", BookmarkBrush);

    private Brush Themed(DependencyProperty property) => (Brush)GetValue(property);
    private const double BookmarkHeight = 11;
    private static readonly Typeface LabelFace = new("Segoe UI");

    private static readonly double[] TickSteps =
        [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];

    private enum Drag { None, Playhead, In, Out }
    private Drag _drag;
    private TimelineState? _state;

    public event Action<double, bool>? SeekRequested; // time, final (mouse released)
    internal event Action<ClipBookmark>? BookmarkActivated;
    private IReadOnlyList<ClipBookmark> _bookmarks = [];

    internal void SetBookmarks(IReadOnlyList<ClipBookmark> bookmarks)
    {
        _bookmarks = bookmarks;
        ToolTip = null;
        InvalidateVisual();
    }

    public TimelineState? State
    {
        get => _state;
        set
        {
            if (_state != null) { _state.Changed -= InvalidateVisual; _state.PositionChanged -= InvalidateVisual; }
            _state = value;
            if (_state != null) { _state.Changed += InvalidateVisual; _state.PositionChanged += InvalidateVisual; }
            InvalidateVisual();
        }
    }

    public bool HasMedia { get; set; }

    public TimelineBar()
    {
        Height = 44;
        Cursor = Cursors.Hand;
        ClipToBounds = true;
        Focusable = false;
        SetResourceReference(ThemeSurfaceProperty, "SurfaceBrush");
        SetResourceReference(ThemeTextProperty, "SecondaryTextBrush");
        SetResourceReference(ThemeGridProperty, "BorderBrush");
        SetResourceReference(ThemeHandleProperty, "WarningBrush");
        SetResourceReference(ThemePlayheadProperty, "PrimaryTextBrush");
        SetResourceReference(ThemeBookmarkProperty, "AccentBrush");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRoundedRectangle(Themed(ThemeSurfaceProperty), null, new Rect(0, 0, w, h), 4, 4);
        var s = _state;
        if (s == null || !HasMedia || w <= 0) return;

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Dynamic resources invalidate the control when the theme changes.
        var tickPen = new Pen(Themed(ThemeGridProperty), 1);
        var playheadPen = new Pen(Themed(ThemePlayheadProperty), 1.5);

        // Ticks + labels
        double step = TickSteps.FirstOrDefault(st => st / s.ViewSpan * w >= 80, TickSteps[^1]);
        double minor = step / 5;
        double first = Math.Floor(s.ViewStart / minor) * minor;
        for (double t = first; t <= s.ViewEnd + minor; t += minor)
        {
            double x = Math.Round(s.TimeToX(t, w)) + 0.5;
            if (x < 0 || x > w) continue;
            bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
            dc.DrawLine(tickPen, new Point(x, h - (major ? 12 : 6)), new Point(x, h));
            if (major)
            {
                var ft = new FormattedText(Label(t, step), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelFace, 10.5, Themed(ThemeTextProperty), dpi);
                dc.DrawText(ft, new Point(x + 3, h - 14 - ft.Height + 2));
            }
        }

        // Selection
        double xi = s.TimeToX(s.In, w), xo = s.TimeToX(s.Out, w);
        if (xi > 0) dc.DrawRectangle(OutsideShade, null, new Rect(0, 0, Math.Min(xi, w), h));
        if (xo < w) dc.DrawRectangle(OutsideShade, null, new Rect(Math.Max(xo, 0), 0, w - Math.Max(xo, 0), h));
        var sel = new Rect(new Point(Math.Clamp(xi, 0, w), 0), new Point(Math.Clamp(xo, 0, w), h));
        if (sel.Width > 0) dc.DrawRectangle(SelectionFill, null, sel);
        DrawHandle(dc, xi, h, isIn: true);
        DrawHandle(dc, xo, h, isIn: false);

        // Playhead
        double xp = Math.Round(s.TimeToX(s.Position, w)) + 0.5;
        if (xp >= -6 && xp <= w + 6)
        {
            dc.DrawLine(playheadPen, new Point(xp, 0), new Point(xp, h));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(xp - 6, 0), true, true);
                g.LineTo(new Point(xp + 6, 0), false, false);
                g.LineTo(new Point(xp, 8), false, false);
            }
            tri.Freeze();
            dc.DrawGeometry(Themed(ThemePlayheadProperty), null, tri);
        }

        foreach (var mark in _bookmarks)
        {
            if (mark.Seconds >= s.Duration || mark.Seconds < s.ViewStart || mark.Seconds > s.ViewEnd) continue;
            double x = BookmarkX(s, mark, w);
            dc.DrawRoundedRectangle(Themed(ThemeBookmarkProperty), null, new Rect(x - 4, 0, 8, BookmarkHeight), 2, 2);
        }
    }

    private void DrawHandle(DrawingContext dc, double x, double h, bool isIn)
    {
        if (x < -10 || x > 1e6) return;
        dc.DrawRectangle(Themed(ThemeHandleProperty), null, new Rect(isIn ? x : x - 3, 0, 3, h));
        // Small tab pointing into the selection
        dc.DrawRectangle(Themed(ThemeHandleProperty), null, new Rect(isIn ? x : x - 9, 0, 9, 3));
        dc.DrawRectangle(Themed(ThemeHandleProperty), null, new Rect(isIn ? x : x - 9, h - 3, 9, 3));
    }

    private static string Label(double t, double step)
    {
        var ts = TimeSpan.FromSeconds(Math.Round(t, 3));
        string head = ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{ts.Minutes}:{ts.Seconds:00}";
        if (step < 0.1) return $"{head}.{ts.Milliseconds:000}";
        if (step < 1) return $"{head}.{ts.Milliseconds / 100}";
        return head;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var s = _state;
        if (s == null || !HasMedia) return;
        double w = ActualWidth;
        var position = e.GetPosition(this);
        if (BookmarkAt(s, position) is { } bookmark)
        {
            BookmarkActivated?.Invoke(bookmark);
            e.Handled = true;
            return;
        }
        double x = position.X;
        double xi = s.TimeToX(s.In, w), xo = s.TimeToX(s.Out, w);
        const double grab = 7;

        if (Math.Abs(x - xo) <= grab && Math.Abs(x - xo) <= Math.Abs(x - xi)) _drag = Drag.Out;
        else if (Math.Abs(x - xi) <= grab) _drag = Drag.In;
        else
        {
            _drag = Drag.Playhead;
            SeekRequested?.Invoke(s.XToTime(x, w), false);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var s = _state;
        if (s == null || !HasMedia) return;
        double w = ActualWidth;
        var position = e.GetPosition(this);
        double x = position.X;
        if (_drag == Drag.None)
        {
            var mark = BookmarkAt(s, position);
            if (!Equals(ToolTip, mark?.TimelineTooltip)) ToolTip = mark?.TimelineTooltip;
            double xi = s.TimeToX(s.In, w), xo = s.TimeToX(s.Out, w);
            Cursor = mark == null && (Math.Abs(x - xi) <= 7 || Math.Abs(x - xo) <= 7) ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        double t = s.XToTime(x, w);
        switch (_drag)
        {
            case Drag.Playhead: SeekRequested?.Invoke(t, false); break;
            case Drag.In: s.DragIn(t); SeekRequested?.Invoke(s.In, false); break;
            case Drag.Out: s.DragOut(t); SeekRequested?.Invoke(s.Out, false); break;
        }
    }

    private ClipBookmark? BookmarkAt(TimelineState state, Point position)
    {
        if (position.Y > BookmarkHeight || ActualWidth <= 0) return null;
        ClipBookmark? nearest = null;
        double distance = 6;
        foreach (var mark in _bookmarks)
        {
            if (mark.Seconds >= state.Duration || mark.Seconds < state.ViewStart || mark.Seconds > state.ViewEnd) continue;
            double x = BookmarkX(state, mark, ActualWidth);
            double delta = Math.Abs(x - position.X);
            if (delta > distance) continue;
            distance = delta;
            nearest = mark;
        }
        return nearest;
    }

    private static double BookmarkX(TimelineState state, ClipBookmark mark, double width)
    {
        double inset = Math.Min(4, width / 2);
        return Math.Clamp(state.TimeToX(mark.Seconds, width), inset, width - inset);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        if (_drag == Drag.Playhead && _state != null)
            SeekRequested?.Invoke(_state.XToTime(e.GetPosition(this).X, ActualWidth), true);
        ReleaseMouseCapture(); // finishes the drag in OnLostMouseCapture
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        var drag = _drag;
        _drag = Drag.None;
        if (drag != Drag.None && _state is { } s)
            SeekRequested?.Invoke(drag switch { Drag.In => s.In, Drag.Out => s.Out, _ => s.Position }, true);
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e) => HandleWheel(this, _state, e);

    /// <summary>Shared wheel behaviour for the ruler and the waveform lanes.</summary>
    public static void HandleWheel(FrameworkElement el, TimelineState? s, MouseWheelEventArgs e)
    {
        if (s != null && ApplyWheel(s, Keyboard.Modifiers, e.Delta, e.GetPosition(el).X, el.ActualWidth))
            e.Handled = true;
    }

    internal static bool ApplyWheel(TimelineState state, ModifierKeys modifiers, int delta, double x, double width)
    {
        if (state.Duration <= 0 || width <= 0 || delta == 0) return false;
        double steps = delta / 120.0;
        if ((modifiers & ModifierKeys.Control) != 0)
            state.ZoomAt(state.XToTime(Math.Clamp(x, 0, width), width), Math.Pow(0.8, steps));
        else if ((modifiers & ModifierKeys.Shift) != 0)
            state.Pan(-steps * state.ViewSpan * 0.15);
        else return false; // leave ordinary wheel input to the containing ScrollViewer
        return true;
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
