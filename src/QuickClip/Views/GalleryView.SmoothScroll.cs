using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickClip.Views;

public partial class GalleryView
{
    private ScrollViewer? _animatedGalleryScroll;
    private double _galleryScrollTarget;
    private long _galleryScrollFrameTick;
    private long _lastGalleryWheelMs;
    private bool _galleryScrollAnimating;
    private readonly HashSet<FrameworkElement> _deferredThumbnailCards = [];

    private bool DeferThumbnailWhileScrolling(FrameworkElement card)
    {
        if (!_galleryScrollAnimating) return false;
        // Do not start FFmpeg or image decoding for cards that are being recycled
        // rapidly. Load only the final visible cards when the scroll settles.
        _deferredThumbnailCards.Add(card);
        return true;
    }

    private void FlushDeferredThumbnails()
    {
        if (_deferredThumbnailCards.Count == 0) return;
        FrameworkElement[] pending = [.. _deferredThumbnailCards];
        _deferredThumbnailCards.Clear();
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_released || _galleryScrollAnimating) return;
            foreach (var card in pending)
            {
                if (!card.IsLoaded || !card.IsVisible || card.ActualHeight <= 0) continue;
                Point pos = card.TranslatePoint(new Point(), ClipArea);
                if (pos.Y + card.ActualHeight < 0 || pos.Y > ClipArea.ActualHeight) continue;
                RequestThumbnail(card);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // Wheel notches queue a pixel target, not whole card/day rows. A time-based
    // interpolation makes the motion stable on both 60 Hz and high-refresh screens.
    private void SmoothGalleryWheel(MouseWheelEventArgs e)
    {
        var scroll = Rows.Template?.FindName("GalleryScroll", Rows) as ScrollViewer;
        if (scroll == null || scroll.ScrollableHeight <= 0) return;
        _lastGalleryWheelMs = Environment.TickCount64;
        _hoverPreview.Stop();

        if (!_galleryScrollAnimating || !ReferenceEquals(scroll, _animatedGalleryScroll))
        {
            StopGalleryWheel();
            _animatedGalleryScroll = scroll;
            _galleryScrollTarget = scroll.VerticalOffset;
            _galleryScrollFrameTick = Stopwatch.GetTimestamp();
            _galleryScrollAnimating = true;
            CompositionTarget.Rendering += OnGalleryScrollFrame;
        }

        double wheelLines = SystemParameters.WheelScrollLines;
        double perNotch = wheelLines < 0 ? scroll.ViewportHeight : Math.Clamp(wheelLines, 1, 6) * 34;
        _galleryScrollTarget = Math.Clamp(_galleryScrollTarget - e.Delta / 120d * perNotch,
            0, scroll.ScrollableHeight);
        e.Handled = true;
    }

    private void OnGalleryScrollFrame(object? sender, EventArgs e)
    {
        var scroll = _animatedGalleryScroll;
        if (_released || !IsVisible || scroll == null)
        {
            StopGalleryWheel();
            return;
        }
        long now = Stopwatch.GetTimestamp();
        double deltaSeconds = (now - _galleryScrollFrameTick) / (double)Stopwatch.Frequency;
        _galleryScrollFrameTick = now;
        _galleryScrollTarget = Math.Clamp(_galleryScrollTarget, 0, scroll.ScrollableHeight);
        double next = SmoothGalleryOffset(scroll.VerticalOffset, _galleryScrollTarget, deltaSeconds);
        scroll.ScrollToVerticalOffset(next);
        if (Math.Abs(_galleryScrollTarget - next) < .55)
        {
            scroll.ScrollToVerticalOffset(_galleryScrollTarget);
            StopGalleryWheel();
        }
    }

    internal static double SmoothGalleryOffset(double current, double target, double deltaSeconds)
    {
        // 12 Hz exponential response; cap long-frame jumps if thumbnail work stalls the UI.
        double ratio = 1 - Math.Exp(-12 * Math.Clamp(deltaSeconds, 0, .05));
        return current + (target - current) * ratio;
    }

    private void StopGalleryWheel()
    {
        if (_galleryScrollAnimating) CompositionTarget.Rendering -= OnGalleryScrollFrame;
        _galleryScrollAnimating = false;
        _animatedGalleryScroll = null;
        FlushDeferredThumbnails();
    }
}
