using System.Windows.Controls;
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
    public async Task Leaving_or_recycling_cancels_pending_preview_and_next_card_owns_it()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
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
