using System.Globalization;
using System.Windows;

namespace QuickClip.Shell;

internal enum OverlayAnchor { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
internal enum OverlayAnimation { Ripple, Fade, Slide, None, Grow, SoftPop, Lift }
internal enum OverlayStyle { Compact, Badge, Detailed, ImageOnly }
internal enum OverlayImageFit { Contain, Cover }
internal enum ClipEvent { ReplayEnabled, Saved, DesktopStarted, DesktopStopped, Failed, Inactive, GameStarted }
internal sealed record ClipEventOptions(bool ReplayEnabled = true, bool Saved = true, bool DesktopStarted = true,
    bool DesktopStopped = true, bool Failed = true, bool GameStarted = true)
{
    internal bool Allows(ClipEvent kind, bool inactive) => kind switch
    {
        ClipEvent.ReplayEnabled => ReplayEnabled, ClipEvent.GameStarted => GameStarted, ClipEvent.Saved => Saved, ClipEvent.DesktopStarted => DesktopStarted,
        ClipEvent.DesktopStopped => DesktopStopped, ClipEvent.Failed => Failed, ClipEvent.Inactive => inactive, _ => false,
    };
}

internal enum ClipNotificationMode { Windows, Overlay, Both }

// Independent image placement and timing. Nullable on old profiles for migration.
internal sealed record ClipImageAppearance
{
    public string? Display { get; init; }
    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.TopRight;
    public double Size { get; init; } = 72;
    public double Opacity { get; init; } = 1;
    public double Seconds { get; init; } = DefaultSeconds;
    internal const double DefaultSeconds = 2.59;
    public OverlayAnimation Animation { get; init; } = OverlayAnimation.Fade;
}

internal sealed record ClipOverlayOptions
{
    public OverlayStyle Style { get; init; }
    public bool ShowResolution { get; init; } = true;
    public bool ShowFps { get; init; } = true;
    public bool ShowHotkey { get; init; }
    public bool ImageOnly { get; init; }
    public bool HideSavedBannerFromCapture { get; init; }
    public bool HideImageFromCapture { get; init; }
    public ClipImageAppearance? ImageAppearance { get; init; }
    internal bool WantsImage => ImageOnly || Style == OverlayStyle.ImageOnly;
    public OverlayImageFit ImageFit { get; init; } = OverlayImageFit.Contain;
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
    public double Seconds { get; init; } = 1.5;
    internal const double MinSeconds = .6;
    internal const double MaxSeconds = 60;

    internal static bool TryParseSize(string? text, out double size) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out size)) &&
        double.IsFinite(size) && size >= 24 && size <= 240;

    internal ClipOverlayOptions RestoreDefaultImage() =>
        Migrate() with { ImagePath = null, ImageAppearance = (Migrate().ImageAppearance ?? new()) with { Seconds = ClipImageAppearance.DefaultSeconds } };

    internal static bool TryParseSeconds(string? text, out double seconds) =>
        (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) ||
         double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) &&
        double.IsFinite(seconds) && seconds >= MinSeconds && seconds <= MaxSeconds;
    public OverlayAnimation Animation { get; init; } = OverlayAnimation.Slide;

    internal ClipOverlayOptions Normalize() => this with
    {
        // Badge is retired; old serialized preferences continue to load as Compact.
        Style = Style == OverlayStyle.Badge ? OverlayStyle.Compact
            : Enum.IsDefined(Style) ? Style : OverlayStyle.Compact,
        Anchor = Enum.IsDefined(Anchor) ? Anchor : OverlayAnchor.BottomRight,
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 24, 240) : 72,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .2, 1) : .9,
        ImageFit = Enum.IsDefined(ImageFit) ? ImageFit : OverlayImageFit.Contain,
        Seconds = double.IsFinite(Seconds) ? Math.Clamp(Seconds, MinSeconds, MaxSeconds) : 1.5,
        // Center has no outside screen edge to slide from; use a readable zoom instead.
        Animation = Anchor == OverlayAnchor.Center && Animation == OverlayAnimation.Slide
            ? OverlayAnimation.Grow
            : Enum.IsDefined(Animation) ? Animation : OverlayAnimation.Ripple,
    };

    // Preserve old image-only sizing/timing, but do not inherit its long duration
    // into recording-start/stop banners. Non-image legacy banner settings remain.
    internal ClipOverlayOptions Migrate()
    {
        if (ImageAppearance != null) return this;
        var old = Normalize();
        if (!old.WantsImage) return old with { ImageAppearance = new() };
        return old with
        {
            ImageAppearance = new() { Display = old.Display, Anchor = old.Anchor, Size = old.Size,
                Opacity = old.Opacity, Seconds = old.Seconds, Animation = old.Animation },
            Anchor = OverlayAnchor.BottomRight, Size = 72, Opacity = .9, Seconds = 1.5,
            Animation = OverlayAnimation.Slide,
        };
    }

    internal ClipOverlayOptions Banner(bool detailed = false)
    {
        var options = Migrate().Normalize();
        return options with { Style = detailed ? OverlayStyle.Detailed : OverlayStyle.Compact,
            ImageOnly = false, ImagePath = null,
            Anchor = options.Anchor == OverlayAnchor.Center ? OverlayAnchor.BottomRight : options.Anchor };
    }

    internal ClipOverlayOptions Image()
    {
        var options = Migrate();
        var image = options.ImageAppearance ?? new();
        return (options with { Style = OverlayStyle.ImageOnly, ImageOnly = true,
            Display = image.Display, Anchor = image.Anchor, Size = image.Size, Opacity = image.Opacity,
            Seconds = image.Seconds, Animation = image.Animation }).Normalize();
    }

    internal bool ExcludeFromCapture(bool saved) => WantsImage ? HideImageFromCapture : saved && HideSavedBannerFromCapture;

    internal ClipOverlayOptions? ForEvent(ClipEvent kind)
    {
        var options = Migrate();
        if (kind == ClipEvent.Saved && options.WantsImage && (!string.IsNullOrWhiteSpace(options.ImagePath) || ClipOverlayImage.HasDefault))
            return options.Image();
        if (!options.Enabled) return null;
        return options.Banner(kind == ClipEvent.Saved && !options.WantsImage && options.Style == OverlayStyle.Detailed);
    }

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
            AppProfile.IsPersonal && visuals && settings.ClipOverlay?.ForEvent(kind) != null,
            kind switch
            {
                ClipEvent.ReplayEnabled => "Instant replay enabled",
                ClipEvent.GameStarted => "Game recording started",
                ClipEvent.DesktopStarted => "Desktop recording started",
                ClipEvent.DesktopStopped => "Desktop recording stopped",
                ClipEvent.Saved => "Clip saved",
                ClipEvent.Inactive => "No active recording",
                _ => "Clip not saved",
            }, message);
    }
}
