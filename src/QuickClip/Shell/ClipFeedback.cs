using System.Globalization;
using System.Windows;

namespace QuickClip.Shell;

internal enum OverlayAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
internal enum OverlayAnimation { Ripple, Fade, Slide, None }
internal enum OverlayStyle { Compact, Badge, Detailed }
internal enum ClipEvent { ReplayEnabled, Saved, DesktopStarted, DesktopStopped, Failed, Inactive }
internal sealed record ClipEventOptions(bool ReplayEnabled = true, bool Saved = true, bool DesktopStarted = true,
    bool DesktopStopped = true, bool Failed = true)
{
    internal bool Allows(ClipEvent kind, bool inactive) => kind switch
    {
        ClipEvent.ReplayEnabled => ReplayEnabled, ClipEvent.Saved => Saved, ClipEvent.DesktopStarted => DesktopStarted,
        ClipEvent.DesktopStopped => DesktopStopped, ClipEvent.Failed => Failed, ClipEvent.Inactive => inactive, _ => false,
    };
}

internal enum ClipNotificationMode { Windows, Overlay, Both }

internal sealed record ClipOverlayOptions
{
    public OverlayStyle Style { get; init; }
    public bool ShowResolution { get; init; } = true;
    public bool ShowFps { get; init; } = true;
    public bool ShowHotkey { get; init; }
    public bool ImageOnly { get; init; }
    public bool ShowGame { get; init; } = true;
    public bool ShowTitle { get; init; } = true;
    public bool ShowSource { get; init; } = true;
    public bool ShowDuration { get; init; } = true;
    public string? ImagePath { get; init; }
    public bool Enabled { get; init; }
    public string? Display { get; init; }
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.BottomRight;
    public double Size { get; init; } = 72;
    public double Opacity { get; init; } = .9;
    public double Seconds { get; init; } = 1.4;
    internal const double MinSeconds = .6;
    internal const double MaxSeconds = 60;

    internal static bool TryParseSeconds(string? text, out double seconds) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) &&
        double.IsFinite(seconds) && seconds >= MinSeconds && seconds <= MaxSeconds;
    public OverlayAnimation Animation { get; init; } = OverlayAnimation.Slide;

    internal ClipOverlayOptions Normalize() => this with
    {
        Style = Enum.IsDefined(Style) ? Style : OverlayStyle.Compact,
        Anchor = Enum.IsDefined(Anchor) ? Anchor : OverlayAnchor.BottomRight,
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 40, 120) : 72,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .2, 1) : .9,
        Seconds = double.IsFinite(Seconds) ? Math.Clamp(Seconds, MinSeconds, MaxSeconds) : 1.4,
        Animation = Enum.IsDefined(Animation) ? Animation : OverlayAnimation.Ripple,
    };

    internal static Point Place(Rect display, double size, double margin, OverlayAnchor anchor)
        => Place(display, new Size(size, size), margin, anchor);

    internal static Point Place(Rect display, Size size, double margin, OverlayAnchor anchor)
    {
        int position = Enum.IsDefined(anchor) ? (int)anchor : (int)OverlayAnchor.BottomRight;
        double Align(double origin, double extent, double itemSize, int alignment)
        {
            double room = Math.Max(0, extent - itemSize);
            double edge = Math.Min(margin, room / 2);
            return origin + (alignment == 0 ? edge : alignment == 1 ? room / 2 : room - edge);
        }
        return new(Align(display.Left, display.Width, size.Width, position % 3), Align(display.Top, display.Height, size.Height, position / 3));
    }
}

internal sealed record ClipNotificationDetails(string? Game, string? Title, string? Source, string? Duration,
    string? Resolution = null, string? Fps = null, string? Hotkey = null)
{
    internal string[] Lines(ClipOverlayOptions options) =>
        new[] { options.ShowTitle ? Title : null, options.ShowGame ? Game : null,
            options.ShowSource ? Source : null, options.ShowDuration ? Duration : null,
            options.ShowResolution ? Resolution : null, options.ShowFps ? Fps : null, options.ShowHotkey ? Hotkey : null }
        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

internal sealed record ClipFeedback(bool Success, bool Sound, bool Windows, bool Overlay, string Title, string Message)
{
    internal static ClipFeedback From(AppSettings settings, bool success, string message) =>
        From(settings, success ? ClipEvent.Saved : ClipEvent.Failed, message);

    internal static ClipFeedback From(AppSettings settings, ClipEvent kind, string message)
    {
        bool allowed = !AppProfile.IsPersonal ||
            (settings.ClipEvents ?? new()).Allows(kind, settings.ShowInactiveSaveNotifications);
        bool visuals = allowed && (!AppProfile.IsPersonal || settings.ClipNotificationsEnabled);
        return new(kind is not (ClipEvent.Failed or ClipEvent.Inactive),
            allowed && kind == ClipEvent.Saved && settings.ClipSound,
            visuals && settings.ClipWindowsNotification,
            AppProfile.IsPersonal && visuals && settings.ClipOverlay?.Enabled == true,
            kind switch
            {
                ClipEvent.ReplayEnabled => "Instant replay enabled",
                ClipEvent.DesktopStarted => "Desktop recording started",
                ClipEvent.DesktopStopped => "Desktop recording stopped",
                ClipEvent.Saved => "Clip saved",
                ClipEvent.Inactive => "No active recording",
                _ => "Clip not saved",
            }, message);
    }
}
