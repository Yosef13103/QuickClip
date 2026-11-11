using System.Windows;

namespace QuickClip.Shell;

internal enum OverlayAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
internal enum OverlayAnimation { Ripple, Fade }

internal sealed record ClipOverlayOptions
{
    public bool Enabled { get; init; }
    public string? Display { get; init; }
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.BottomRight;
    public double Size { get; init; } = 72;
    public double Opacity { get; init; } = .9;
    public double Seconds { get; init; } = 1.4;
    public OverlayAnimation Animation { get; init; }

    internal ClipOverlayOptions Normalize() => this with
    {
        Anchor = Enum.IsDefined(Anchor) ? Anchor : OverlayAnchor.BottomRight,
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 40, 120) : 72,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .2, 1) : .9,
        Seconds = double.IsFinite(Seconds) ? Math.Clamp(Seconds, .6, 5) : 1.4,
        Animation = Enum.IsDefined(Animation) ? Animation : OverlayAnimation.Ripple,
    };

    internal static Point Place(Rect display, double size, double margin, OverlayAnchor anchor)
    {
        int position = Enum.IsDefined(anchor) ? (int)anchor : (int)OverlayAnchor.BottomRight;
        double Align(double origin, double extent, int alignment)
        {
            double room = Math.Max(0, extent - size);
            double edge = Math.Min(margin, room / 2);
            return origin + (alignment == 0 ? edge : alignment == 1 ? room / 2 : room - edge);
        }
        return new(Align(display.Left, display.Width, position % 3), Align(display.Top, display.Height, position / 3));
    }
}

internal sealed record ClipFeedback(bool Success, bool Sound, bool Windows, bool Overlay, string Title, string Message)
{
    internal static ClipFeedback From(AppSettings settings, bool success, string message) =>
        new(success, success && settings.ClipSound, settings.ClipWindowsNotification,
            AppProfile.IsPersonal && settings.ClipOverlay?.Enabled == true,
            success ? "Clip saved" : "Clip not saved", message);
}
