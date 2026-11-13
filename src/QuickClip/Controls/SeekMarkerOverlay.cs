using System.Windows;
using System.Windows.Media;
using QuickClip.Gallery;

namespace QuickClip.Controls;

/// <summary>Passive clip-player timeline diamonds. Source video, audio and the seek slider are unchanged.</summary>
public sealed class SeekMarkerOverlay : FrameworkElement
{
    private static readonly Brush MarkerBrush = CreateMarkerBrush();
    private IReadOnlyList<ClipBookmark> _markers = [];
    private double _duration;

    internal void Show(IReadOnlyList<ClipBookmark> markers, double duration)
    {
        _markers = markers;
        UpdateDuration(duration);
    }

    internal void UpdateDuration(double duration)
    {
        _duration = double.IsFinite(duration) && duration > 0 ? duration : 0;
        InvalidateVisual();
    }

    internal static double Position(double seconds, double duration, double width)
    {
        if (duration <= 0 || width <= 0 || !double.IsFinite(seconds) || !double.IsFinite(duration)) return 0;
        double inset = Math.Min(8, width / 2);
        return inset + Math.Clamp(seconds / duration, 0, 1) * (width - 2 * inset);
    }

    internal string? TooltipAt(Point location)
    {
        if (_duration <= 0 || ActualWidth <= 0 || Math.Abs(location.Y - ActualHeight / 2) > 9) return null;
        var mark = _markers.Where(marker => double.IsFinite(marker.Seconds) && marker.Seconds >= 0 && marker.Seconds < _duration)
            .MinBy(marker => Math.Abs(Position(marker.Seconds, _duration, ActualWidth) - location.X));
        if (mark == null || Math.Abs(Position(mark.Seconds, _duration, ActualWidth) - location.X) > 9) return null;
        return mark.TimelineTooltip;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_duration <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        double y = ActualHeight / 2;
        foreach (var marker in _markers)
        {
            if (!double.IsFinite(marker.Seconds) || marker.Seconds < 0 || marker.Seconds >= _duration) continue;
            double x = Position(marker.Seconds, _duration, ActualWidth);
            var diamond = new StreamGeometry();
            using (var geometry = diamond.Open())
            {
                geometry.BeginFigure(new Point(x, y - 6), true, true);
                geometry.LineTo(new Point(x + 6, y), true, false);
                geometry.LineTo(new Point(x, y + 6), true, false);
                geometry.LineTo(new Point(x - 6, y), true, false);
            }
            diamond.Freeze();
            dc.DrawGeometry(MarkerBrush, null, diamond);
        }
    }

    private static Brush CreateMarkerBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x4B, 0xC3, 0xEC));
        brush.Freeze();
        return brush;
    }
}
