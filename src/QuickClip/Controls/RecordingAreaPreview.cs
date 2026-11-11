using System.Windows;
using System.Windows.Media;
using QuickClip.Recording;

namespace QuickClip.Controls;

/// <summary>Draws a schematic from monitor dimensions and crop coordinates. It never opens a capture session.</summary>
public sealed class RecordingAreaPreview : FrameworkElement
{
    private static readonly Brush ExcludedBrush = FrozenBrush(Color.FromRgb(34, 37, 44));
    private static readonly Brush IncludedBrush = FrozenBrush(Color.FromRgb(48, 83, 102));
    private static readonly Brush EdgeBrush = FrozenBrush(Color.FromRgb(119, 202, 242));
    private static readonly Pen Edge = FrozenPen();
    private (int Width, int Height, RecordingArea Area) _geometry;

    internal void SetArea(int width, int height, RecordingArea area)
    {
        if (_geometry == (width, height, area)) return;
        _geometry = (width, height, area);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var (width, height, area) = _geometry;
        var (source, crop) = PreviewBounds(width, height, area, RenderSize);
        if (source.IsEmpty) return;
        bool highContrast = SystemParameters.HighContrast;
        context.DrawRectangle(highContrast ? SystemColors.WindowBrush : ExcludedBrush, null, source);
        context.DrawRectangle(highContrast ? SystemColors.HighlightBrush : IncludedBrush,
            highContrast ? new Pen(SystemColors.WindowTextBrush, 1.5) : Edge, crop);
    }

    internal static (Rect Source, Rect Crop) PreviewBounds(int width, int height, RecordingArea area, Size viewport)
    {
        if (width <= 0 || height <= 0 || viewport.Width <= 0 || viewport.Height <= 0)
            return (Rect.Empty, Rect.Empty);
        double scale = Math.Min(viewport.Width / width, viewport.Height / height);
        var source = new Rect((viewport.Width - width * scale) / 2, (viewport.Height - height * scale) / 2,
            width * scale, height * scale);
        var crop = new Rect(source.X + area.X * scale, source.Y + area.Y * scale, area.Width * scale, area.Height * scale);
        return (source, crop);
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen()
    {
        var pen = new Pen(EdgeBrush, 1.5);
        pen.Freeze();
        return pen;
    }
}
