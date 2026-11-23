using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickClip.Gallery;

namespace QuickClip.Views;

public partial class GalleryView
{
    private const double CardGap = 14; // must match the card's right margin
    private const int PreferredCardWidth = 440;
    private int _preferredThumbnailWidth = PreferredCardWidth;
    private int _savedThumbnailWidth;
    private readonly DispatcherTimer _zoomSettleTimer;
    private DispatcherOperation? _zoomPreview;
    private bool _galleryZoomPending;
    private Task? _zoomSave;

    private ScrollViewer? GalleryScroll => Rows.Template?.FindName("GalleryScroll", Rows) as ScrollViewer;

    internal static int ColumnsForWidth(double available, int preferred = PreferredCardWidth) =>
        Math.Clamp((int)(available / (preferred + CardGap)), 1, preferred >= PreferredCardWidth ? 4 : 8);

    internal static int ZoomCardWidth(double displayedWidth, int preferred, int wheelDelta)
    {
        if (wheelDelta == 0) return preferred;
        if (preferred == PreferredCardWidth && (wheelDelta > 0 && displayedWidth >= 800 ||
            wheelDelta < 0 && displayedWidth <= 180)) return preferred;
        // Default cards stretch to fill each row. Start the first wheel movement
        // from the visible width, not the nominal 440px setting.
        int current = preferred == PreferredCardWidth
            ? Math.Clamp((int)Math.Round(displayedWidth), 180, 800)
            : Math.Clamp(preferred, 180, 800);
        int direction = Math.Sign(wheelDelta);
        int step = Math.Max(1, (int)Math.Round(Math.Abs(wheelDelta / 120d) * 40));
        int target = Math.Clamp(current + direction * step, 180, 800);
        // 440 is reserved for automatic sizing (Ctrl+0), even when zooming through it.
        return target == PreferredCardWidth ? target + direction : target;
    }

    private void ClipArea_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SmoothGalleryWheel(e);
            return;
        }
        ZoomGalleryWheel(e.Delta);
        e.Handled = true; // Ctrl+wheel changes thumbnail size, not the scroll position.
    }

    internal void ZoomGalleryWheel(int delta)
    {
        StopGalleryWheel();
        SetGalleryZoom(ZoomCardWidth(CardWidth, _preferredThumbnailWidth, delta));
    }

    private void SetGalleryZoom(int preferred)
    {
        if (_released || preferred == _preferredThumbnailWidth) return;
        _galleryZoomPending = true;
        _preferredThumbnailWidth = preferred;
        _lastGalleryWheelMs = Environment.TickCount64;
        _hoverPreview.Stop();
        _zoomSettleTimer.Stop();
        _zoomSettleTimer.Start();
        // Update the existing virtualized cards once per dispatcher render pass.
        _zoomPreview ??= Dispatcher.InvokeAsync(() =>
        {
            _zoomPreview = null;
            LayoutCards(ClipArea.ActualWidth, preview: true);
        }, DispatcherPriority.Render);
    }

    private void ClipArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        var scroll = GalleryScroll;
        double offset = scroll?.VerticalOffset ?? 0;
        if (LayoutCards(e.NewSize.Width, _galleryZoomPending) != null && offset > 0)
            scroll?.ScrollToVerticalOffset(offset);
    }

    private static double GalleryLayoutWidth(double available) =>
        Math.Max(1, available - SystemParameters.VerticalScrollBarWidth);

    private List<object>? LayoutCards(double available, bool preview = false)
    {
        if (available <= 0) return null;
        double width = GalleryLayoutWidth(available);
        int columns = ColumnsForWidth(width, _preferredThumbnailWidth);
        double cardWidth = _preferredThumbnailWidth == PreferredCardWidth
            ? Math.Floor(width / columns) - CardGap
            : Math.Min(_preferredThumbnailWidth, width - CardGap);
        // During the gesture cards can resize within their existing rows. Keep
        // every column visible until the final column count is committed.
        if (preview) cardWidth = Math.Min(cardWidth, Math.Floor(width / _columns) - CardGap);
        cardWidth = Math.Max(1, cardWidth);
        if (CardWidth != cardWidth)
        {
            SetValue(CardWidthProperty, cardWidth);
            SetValue(PosterHeightProperty, Math.Max(1, cardWidth - 2) * 9 / 16);
        }
        if (preview || columns == _columns) return null;
        _columns = columns;
        return Rows.ItemsSource == null ? null : ReplaceGalleryRows();
    }

    private List<object> ReplaceGalleryRows()
    {
        var rows = GalleryDayFeed.BuildRows(_days, _columns);
        StopGalleryWheel();
        Rows.ItemsSource = rows;
        _renderedColumns = _columns;
        return rows;
    }

    private async Task FinishGalleryZoomAsync()
    {
        _zoomSettleTimer.Stop();
        _zoomPreview?.Abort();
        _zoomPreview = null;
        try
        {
            var scroll = GalleryScroll;
            double width = ClipArea.ActualWidth;
            var anchor = scroll != null && ColumnsForWidth(GalleryLayoutWidth(width), _preferredThumbnailWidth) != _columns
                ? CaptureZoomAnchor(scroll)
                : null;
            if (LayoutCards(width) is { } rows && anchor != null)
                await RestoreZoomAnchorAsync(scroll!, anchor, rows);
        }
        finally
        {
            if (!_zoomSettleTimer.IsEnabled)
            {
                _galleryZoomPending = false;
                FlushDeferredThumbnails();
            }
        }
        if (!_released && !_zoomSettleTimer.IsEnabled) await SaveGalleryZoomAsync();
    }

    private VirtualizingStackPanel? GalleryRowsPanel =>
        Rows.Template?.FindName("GalleryRowsPresenter", Rows) is ItemsPresenter presenter &&
        VisualTreeHelper.GetChildrenCount(presenter) != 0
            ? VisualTreeHelper.GetChild(presenter, 0) as VirtualizingStackPanel
            : null;

    private sealed record ZoomAnchor(object Item, double HiddenFraction);

    private ZoomAnchor? CaptureZoomAnchor(ScrollViewer scroll)
    {
        if (scroll.VerticalOffset <= 0 || !scroll.IsVisible || GalleryRowsPanel is not { } panel) return null;
        // Inspect only realized containers. The first visible row may be a day header.
        FrameworkElement? first = null;
        double top = double.PositiveInfinity;
        foreach (UIElement child in panel.Children)
        {
            if (child is not FrameworkElement row || row.DataContext is not (GalleryDayHeader or GalleryClipRow)) continue;
            double y = row.TranslatePoint(new Point(), scroll).Y;
            if (y + row.ActualHeight <= 0 || y >= scroll.ViewportHeight || y >= top) continue;
            top = y;
            first = row;
        }
        if (first == null) return null;
        object item = first.DataContext is GalleryClipRow clips ? clips.Clips[0] : first.DataContext;
        return new(item, Math.Clamp(-top / first.ActualHeight, 0, 1));
    }

    private async Task RestoreZoomAnchorAsync(ScrollViewer scroll, ZoomAnchor anchor, List<object> rows)
    {
        int index = rows.FindIndex(row => ReferenceEquals(row, anchor.Item) ||
            row is GalleryClipRow clips && anchor.Item is ClipItem item && clips.Clips.Contains(item));
        if (index < 0) return;

        // Realize the anchor before the next normal layout can show another part of the feed.
        GalleryRowsPanel?.BringIndexIntoViewPublic(index);
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (_released || !scroll.IsVisible || !ReferenceEquals(Rows.ItemsSource, rows) ||
            Rows.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container) return;

        double top = container.TranslatePoint(new Point(), scroll).Y;
        scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top + anchor.HiddenFraction * container.ActualHeight));
        await Dispatcher.Yield(DispatcherPriority.Loaded);
    }

    private async Task SaveGalleryZoomAsync()
    {
        while (_zoomSave is { IsCompleted: false } pending) await pending;
        if (_zoomSettleTimer.IsEnabled) return;
        int width = _preferredThumbnailWidth;
        if (width == _savedThumbnailWidth) return;
        await (_zoomSave = PersistWidthAsync(width));

        async Task PersistWidthAsync(int target)
        {
            var settings = AppSettings.Current;
            settings.GalleryThumbnailWidth = target;
            if (await Task.Run(() => settings.TrySave()))
                _savedThumbnailWidth = target;
            else if (!_released)
                ShowStatus("Could not save the Gallery thumbnail size.");
        }
    }
}
