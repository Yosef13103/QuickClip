using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class GalleryPreviewTests
{
    [Theory]
    [InlineData(700, 1)]
    [InlineData(960, 2)]
    [InlineData(1500, 3)]
    [InlineData(2600, 4)]
    public void Gallery_uses_large_cards_without_forcing_a_single_poster_at_desktop_width(double width, int columns) =>
        Assert.Equal(columns, GalleryView.ColumnsForWidth(width));

    [Theory]
    [InlineData(500, 440, 120, 540)]
    [InlineData(500, 440, -120, 460)]
    [InlineData(500, 320, 120, 360)]
    [InlineData(500, 800, 120, 800)]
    [InlineData(500, 180, -120, 180)]
    [InlineData(400, 400, 120, 441)]
    [InlineData(480, 480, -120, 439)]
    [InlineData(500, 320, 30, 330)]
    [InlineData(500, 440, 0, 440)]
    [InlineData(900, 440, 120, 440)]
    [InlineData(170, 440, -120, 440)]
    public void Gallery_ctrl_wheel_resizes_monotonically_from_visible_card_width(
        double actual, int preferred, int delta, int expected) =>
        Assert.Equal(expected, GalleryView.ZoomCardWidth(actual, preferred, delta));

    [Theory]
    [InlineData(1107.99, 540, 1)]
    [InlineData(1108, 540, 2)]
    [InlineData(2000, 180, 8)]
    public void Manual_columns_fit_card_width_and_gap(double width, int preferred, int expected) =>
        Assert.Equal(expected, GalleryView.ColumnsForWidth(width, preferred));

    [Fact]
    public void Gallery_wheel_interpolation_moves_smoothly_and_does_not_overshoot()
    {
        double current = 0, target = 900;
        for (int frame = 0; frame < 60; frame++)
        {
            double next = GalleryView.SmoothGalleryOffset(current, target, 1d / 60);
            Assert.True(next > current);
            Assert.True(next <= target);
            current = next;
        }
        Assert.InRange(target - current, 0, .01);
        Assert.Equal(75, GalleryView.SmoothGalleryOffset(75, 75, 0));
        Assert.InRange(GalleryView.SmoothGalleryOffset(0, target, 10), 0, target);
    }

    [Fact]
    public async Task Cache_key_distinguishes_pixel_buckets_and_replaced_files()
    {
        var root = Directory.CreateTempSubdirectory("quickclip-thumbnail-");
        try
        {
            string clip = Path.Combine(root.FullName, "fixture.mp4");
            await File.WriteAllTextAsync(clip, "fixture");
            var file = new FileInfo(clip);
            string medium = ThumbnailCache.PathFor(file, 600);
            Assert.Equal(medium, ThumbnailCache.PathFor(file, 720));
            Assert.NotEqual(medium, ThumbnailCache.PathFor(file, 960));
            Assert.Equal(960, ThumbnailCache.WidthFor(2000));
            await File.AppendAllTextAsync(clip, " updated");
            file.Refresh();
            Assert.NotEqual(medium, ThumbnailCache.PathFor(file, 720));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public Task Active_preview_covers_the_thumbnail_with_black_across_the_poster() => OnStaThread(() =>
    {
        var poster = new Grid { Background = Brushes.Red };
        var surface = new GalleryHoverPreview().CreateSurface();
        var media = Assert.IsType<MediaElement>(surface.Child);
        Assert.Equal(Stretch.Uniform, media.Stretch);
        poster.Children.Add(surface);

        var size = new Size(160, 90);
        poster.Measure(size);
        poster.Arrange(new Rect(size));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, PixelAt(0, 0));
        surface.Opacity = 1; // the state reached after MediaOpened
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, PixelAt(0, 0));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, PixelAt(159, 89));

        byte[] PixelAt(int x, int y)
        {
            var rendered = new RenderTargetBitmap(160, 90, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(poster);
            var pixel = new byte[4];
            rendered.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return pixel;
        }
    });

    [Fact]
    public Task Leaving_or_recycling_cancels_pending_preview_and_next_card_owns_it() => OnStaThread(() =>
    {
        var preview = new GalleryHoverPreview();
        var first = new Grid();
        var second = new Grid();
        var file = new FileInfo(Path.Combine(Path.GetTempPath(), $"preview-fixture-{Guid.NewGuid():N}.mp4"));
        File.WriteAllText(file.FullName, "synthetic");
        try
        {
            var item = new ClipItem(file, new ClipDetails(DateTimeOffset.UtcNow, "Fixture"), default);
            first.DataContext = second.DataContext = item;
            preview.Start(first, item);
            Assert.True(preview.IsActive);
            preview.Start(second, item);
            preview.Stop(first);
            Assert.True(preview.IsActive);
            second.DataContext = null;
            Assert.False(preview.IsActive);
            Assert.Empty(first.Children);
            Assert.Empty(second.Children);
        }
        finally { File.Delete(file.FullName); }
    });

    private static async Task OnStaThread(Action test)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                test();
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
