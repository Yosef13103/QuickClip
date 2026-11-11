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
