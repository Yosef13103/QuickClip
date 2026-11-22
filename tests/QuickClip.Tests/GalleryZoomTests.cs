using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Tests;

[CollectionDefinition("Gallery zoom", DisableParallelization = true)]
public sealed class GalleryZoomCollection;

[Collection("Gallery zoom")]
public sealed class GalleryZoomTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public Task Resizing_virtualized_cards_keeps_the_feed_then_reflows_once_at_the_threshold() =>
        WithGallery(async (gallery, rows, scroll, _) =>
    {
        var panel = GetRowsPanel(rows);
        scroll.ScrollToVerticalOffset(scroll.ViewportHeight * 7);
        await WaitUntil(() => scroll.VerticalOffset > scroll.ViewportHeight * 4);
        if (FirstVisibleRow(panel, scroll) is { DataContext: GalleryDayHeader } heading)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + heading.ActualHeight);
            await WaitUntil(() => FirstVisibleRow(panel, scroll).DataContext is GalleryClipRow);
        }

        var anchor = Assert.IsType<GalleryClipRow>(FirstVisibleRow(panel, scroll).DataContext).Clips[0];
        anchor.IsSelected = true;
        var originalSource = rows.ItemsSource;
        var headers = rows.Items.OfType<GalleryDayHeader>().ToArray();
        var selectedDay = Assert.Single(headers, day => day.Clips.Contains(anchor));
        selectedDay.RefreshSelection();
        var order = rows.Items.OfType<GalleryClipRow>().SelectMany(row => row.Clips).ToArray();

        gallery.ZoomGalleryWheel(120);
        await WaitUntil(() => gallery.CardWidth == 580);
        Assert.Same(originalSource, rows.ItemsSource);
        Assert.Contains(anchor, Assert.IsType<GalleryClipRow>(FirstVisibleRow(panel, scroll).DataContext).Clips);

        int replacements = 0;
        var items = (System.Collections.Specialized.INotifyCollectionChanged)rows.Items;
        items.CollectionChanged += (_, args) =>
        {
            if (args.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) replacements++;
        };
        gallery.ZoomGalleryWheel(120);
        gallery.ZoomGalleryWheel(120);
        await WaitUntil(() => !ReferenceEquals(originalSource, rows.ItemsSource) && !ZoomPending(gallery));

        Assert.Equal(660, gallery.CardWidth);
        Assert.Equal(1, replacements);
        Assert.All(rows.Items.OfType<GalleryClipRow>(), row => Assert.Single(row.Clips));
        Assert.Equal(headers, rows.Items.OfType<GalleryDayHeader>());
        Assert.Equal(order, rows.Items.OfType<GalleryClipRow>().SelectMany(row => row.Clips));
        Assert.Same(anchor, Assert.Single(Assert.IsType<GalleryClipRow>(FirstVisibleRow(panel, scroll).DataContext).Clips));
        Assert.True(anchor.IsSelected);
        Assert.Null(selectedDay.Selection);
        Assert.InRange(panel.Children.Count, 1, 40);
    });

    [Fact]
    public Task Zoom_defers_decodes_and_requests_only_the_final_pixel_bucket() =>
        WithGallery(async (gallery, _, _, first) =>
    {
        var decodeSizes = new List<int>();
        var dpi = VisualTreeHelper.GetDpi(gallery).DpiScaleX;
        int finalBucket = ThumbnailCache.WidthFor(780 * dpi);
        first.ThumbnailWidth = 1;
        string cache = ThumbnailCache.PathFor(first.File, finalBucket);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        using (var stream = File.Create(cache))
        {
            var source = BitmapSource.Create(960, 540, 96, 96, PixelFormats.Gray8, null, new byte[960 * 540], 960);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            encoder.Save(stream);
        }
        first.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClipItem.Thumbnail) && first.Thumbnail is BitmapSource image)
                decodeSizes.Add(image.PixelWidth);
        };

        gallery.ZoomGalleryWheel(120);
        await Dispatcher.Yield(DispatcherPriority.Render);
        Assert.Equal(1, first.ThumbnailWidth);
        Assert.Empty(decodeSizes);
        gallery.ZoomGalleryWheel(600);
        await WaitUntil(() => decodeSizes.Count != 0);

        Assert.Equal(780, gallery.CardWidth);
        Assert.Equal(finalBucket, Assert.Single(decodeSizes));
        Assert.True(first.Thumbnail!.IsFrozen);
    });

    [Fact]
    public Task Multiple_notches_update_the_width_before_persisting_the_final_preference() =>
        WithGallery(async (gallery, _, _, _) =>
    {
        gallery.ZoomGalleryWheel(120);
        gallery.ZoomGalleryWheel(120);
        await Dispatcher.Yield(DispatcherPriority.Render);

        Assert.Equal(620, gallery.CardWidth);
        Assert.Equal(540, AppSettings.Current.GalleryThumbnailWidth);
        await WaitUntil(() => AppSettings.Current.GalleryThumbnailWidth == 620);
        Assert.False(ZoomPending(gallery));
    });

    private static bool ZoomPending(GalleryView gallery) =>
        (bool)typeof(GalleryView).GetField("_galleryZoomPending", Private)!.GetValue(gallery)!;

    private static FrameworkElement FirstVisibleRow(Panel panel, ScrollViewer scroll) =>
        panel.Children.OfType<FrameworkElement>()
            .Where(row => row.DataContext is GalleryDayHeader or GalleryClipRow)
            .Select(row => (Row: row, Top: row.TranslatePoint(new Point(), scroll).Y))
            .Where(row => row.Top + row.Row.ActualHeight > 0 && row.Top < scroll.ViewportHeight)
            .OrderBy(row => row.Top).First().Row;

    private static VirtualizingStackPanel GetRowsPanel(ItemsControl rows) =>
        Assert.IsType<VirtualizingStackPanel>(VisualTreeHelper.GetChild(
            Assert.IsType<ItemsPresenter>(rows.Template.FindName("GalleryRowsPresenter", rows)), 0));

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            if (condition()) return;
            await Task.Delay(15, timeout.Token);
        }
    }

    private static async Task WithGallery(Func<GalleryView, ItemsControl, ScrollViewer, ClipItem, Task> test)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await Run();
                    result.SetResult();
                }
                catch (Exception ex) { result.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(15));

        async Task Run()
        {
            var fixture = Directory.CreateTempSubdirectory("quickclip-gallery-zoom-");
            var settings = AppSettings.Current;
            var (previousWidth, previousSidebar, previousRoot, previousSources) =
                (settings.GalleryThumbnailWidth, settings.GallerySidebarWidth, settings.ClipsFolder, settings.AdditionalClipSources);
            settings.GalleryThumbnailWidth = 540;
            settings.GallerySidebarWidth = 230;
            settings.ClipsFolder = Directory.CreateDirectory(Path.Combine(fixture.FullName, "empty")).FullName;
            settings.AdditionalClipSources = [];
            try
            {
                var gallery = new GalleryView();
                var window = new Window
                {
                    Width = 1600, Height = 900, Left = -16000, Top = -16000, ShowActivated = false,
                    ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Content = gallery,
                };
                try
                {
                    window.Show();
                    var rows = Assert.IsType<ItemsControl>(gallery.FindName("Rows"));
                    var scroll = Assert.IsType<ScrollViewer>(rows.Template.FindName("GalleryScroll", rows));
                    await WaitUntil(() => rows.ItemsSource != null &&
                        typeof(GalleryView).GetField("_loading", Private)!.GetValue(gallery) is false &&
                        typeof(GalleryView).GetField("_reloadQueued", Private)!.GetValue(gallery) is false);
                    var at = new DateTimeOffset(DateTime.Today.AddHours(12));
                    var clips = Enumerable.Range(0, 160).Select(i =>
                    {
                        string path = Path.Combine(fixture.FullName, $"{i:D3}.mp4");
                        File.WriteAllBytes(path, [0]);
                        var time = at.AddDays(-i / 40).AddSeconds(-i % 40);
                        return new ClipItem(new(path), new(time, "Fixture"), new(1, time)) { ThumbnailWidth = 960 };
                    }).ToList();
                    typeof(GalleryView).GetField("_clips", Private)!.SetValue(gallery, clips);
                    typeof(GalleryView).GetMethod("ShowFolder", Private)!.Invoke(gallery, [false]);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(160, rows.Items.OfType<GalleryClipRow>().Sum(row => row.Clips.Count));
                    await test(gallery, rows, scroll, clips[0]);
                }
                finally
                {
                    await gallery.CloseAsync();
                    window.Close();
                }
            }
            finally
            {
                settings.GalleryThumbnailWidth = previousWidth;
                settings.GallerySidebarWidth = previousSidebar;
                settings.ClipsFolder = previousRoot;
                settings.AdditionalClipSources = previousSources;
                fixture.Delete(recursive: true);
            }
        }
    }
}
