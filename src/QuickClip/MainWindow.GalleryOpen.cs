using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickClip.Views;

namespace QuickClip;

public partial class MainWindow
{
    private CancellationTokenSource? _galleryOpening;
    private Task? _galleryOpenTask;
    private EditorView? _galleryStagedEditor;
    private GalleryOpenVisual? _galleryOpenVisual;

    private void CancelGalleryOpen()
    {
        _galleryOpening?.Cancel();
        _galleryOpenVisual?.Dispose();
    }

    private Task OpenGalleryCardAsync(string path, FrameworkElement? card)
    {
        if (_closing || !Gallery.IsVisible) return Task.CompletedTask;
        CancelGalleryOpen();
        var previous = _galleryOpenTask;
        var opening = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        _galleryOpening = opening;
        return _galleryOpenTask = OpenGalleryCardCoreAsync(path, card, previous, opening, ++_pageRequest);
    }

    private async Task OpenGalleryCardCoreAsync(string path, FrameworkElement? card, Task? previous,
        CancellationTokenSource opening, long request)
    {
        using (opening)
        {
            var token = opening.Token;
            EditorView? editor = null;
            GalleryOpenVisual? visual = null;
            bool committed = false;
            try
            {
                if (previous != null) await previous;
                if (_closing || !Gallery.IsVisible || request != _pageRequest || token.IsCancellationRequested) return;
                Gallery.StopHoverPreview();
                visual = _galleryOpenVisual = new GalleryOpenVisual(Gallery, card, CancelGalleryOpen);
                var flush = _navigationFlush is { IsCompleted: false } pending ? pending : Gallery.FlushAnnotationsAsync();
                _navigationFlush = flush;
                if (!await flush || token.IsCancellationRequested || request != _pageRequest) return;

                // HwndHost needs a presented HWND. Put the full-size editor outside
                // the client area while Gallery remains the visible page.
                editor = _galleryStagedEditor = EditorPage;
                editor.RenderTransform = new TranslateTransform(-Math.Max(2048, Pages.ActualWidth + 512), 0);
                editor.IsHitTestVisible = false;
                editor.Visibility = Visibility.Visible;
                var order = Gallery.EditorNavigationPaths();
                if (!await editor.PreparePreviewAsync(path, token))
                {
                    if (!token.IsCancellationRequested) Gallery.ShowEditorOpenStatus("Could not open the preview. Select the clip to retry.");
                    return;
                }
                if (token.IsCancellationRequested || _closing || request != _pageRequest || !Gallery.IsVisible) return;
                editor.SetNavigationClips(order);
                visual.Dispose();
                editor.RenderTransform = Transform.Identity;
                editor.IsHitTestVisible = true;
                ShowPage(editor);
                committed = true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                if (request == _pageRequest && !_closing && Gallery.IsVisible && visual?.IsDisposed == false)
                    Gallery.ShowEditorOpenStatus("The preview took too long. Select the clip to retry.");
            }
            catch (Exception ex)
            {
                Log.Failure(DiagnosticCategory.Editor, "Could not prepare Gallery preview", ex);
                if (request == _pageRequest && Gallery.IsVisible)
                    Gallery.ShowEditorOpenStatus("Could not open the preview. Select the clip to retry.");
            }
            finally
            {
                visual?.Dispose();
                if (ReferenceEquals(_galleryOpenVisual, visual)) _galleryOpenVisual = null;
                if (editor != null)
                {
                    if (!committed) editor.Visibility = Visibility.Collapsed;
                    editor.RenderTransform = Transform.Identity;
                    editor.IsHitTestVisible = true;
                    _galleryStagedEditor = null;
                }
                if (ReferenceEquals(_galleryOpening, opening)) _galleryOpening = null;
            }
        }
    }

    private sealed class GalleryOpenVisual : Adorner, IDisposable
    {
        private readonly AdornerLayer? _layer;
        private readonly Grid _layout = new();
        private readonly DispatcherTimer _progress;
        private readonly StackPanel _status = new()
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 24),
            Visibility = Visibility.Collapsed,
        };
        internal bool IsDisposed { get; private set; }

        internal GalleryOpenVisual(FrameworkElement gallery, FrameworkElement? card, Action cancel) : base(gallery)
        {
            _layer = AdornerLayer.GetAdornerLayer(gallery);
            _layout.ClipToBounds = true;
            AddVisualChild(_layout);
            if (SystemParameters.ClientAreaAnimation && card != null) AddSnapshot(gallery, card);

            var text = new TextBlock { Text = "Preparing preview…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            var button = new Button { Content = "Cancel" };
            button.Click += (_, _) => cancel();
            _status.Children.Add(text);
            _status.Children.Add(button);
            _status.SetResourceReference(Panel.BackgroundProperty, "ElevatedSurfaceBrush");
            _layout.Children.Add(_status);
            _progress = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
                (_, _) => { _progress!.Stop(); _status.Visibility = Visibility.Visible; }, Dispatcher);
            _layer?.Add(this);
        }

        private void AddSnapshot(FrameworkElement gallery, FrameworkElement card)
        {
            if (!card.IsVisible || card.ActualWidth < 1 || card.ActualHeight < 1) return;
            try
            {
                Point origin = card.TranslatePoint(new Point(), gallery);
                double width = card.ActualWidth, height = card.ActualHeight;
                var dpi = VisualTreeHelper.GetDpi(card);
                double scale = Math.Min(dpi.DpiScaleX, 768 / Math.Max(width, height));
                var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * scale)),
                    Math.Max(1, (int)Math.Ceiling(height * scale)), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(card);
                bitmap.Freeze();
                _layout.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)), IsHitTestVisible = false,
                });
                var scaleTransform = new ScaleTransform();
                var image = new Image
                {
                    Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                    IsHitTestVisible = false, RenderTransform = scaleTransform,
                    RenderTransformOrigin = new Point(.5, .5),
                    Margin = new Thickness(origin.X, origin.Y, 0, 0),
                };
                _layout.Children.Add(image);
                var expand = new DoubleAnimation(1, 1.18, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, expand);
                scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, expand);
            }
            catch (InvalidOperationException)
            {
                // Virtualized cards can detach during layout. The preview still opens.
            }
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _layout;
        protected override Size MeasureOverride(Size size)
        {
            _layout.Measure(AdornedElement.RenderSize);
            return AdornedElement.RenderSize;
        }

        protected override Size ArrangeOverride(Size size)
        {
            _layout.Arrange(new Rect(AdornedElement.RenderSize));
            return size;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            _progress.Stop();
            _layer?.Remove(this);
        }
    }
}
