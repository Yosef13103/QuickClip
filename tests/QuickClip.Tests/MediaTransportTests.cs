using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using QuickClip.Controls;
using QuickClip.Gallery;
using QuickClip.Themes;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class MediaTransportTests
{
    [Fact]
    public Task Slider_thumb_stays_centered_and_within_track_at_both_endpoints() => OnSta(() =>
    {
        var host = new Grid();
        var controls = new AppVolumeControl();
        new ThemeResources().Apply(host.Resources, ThemeChoice.Slate);
        var slider = new Slider
        {
            Style = (Style)controls.FindResource("ThemedMediaSlider"),
            Minimum = 0, Maximum = 200, IsMoveToPointEnabled = true
        };
        host.Children.Add(slider);
        foreach (double width in new[] { 90d, 180d, 520d })
        foreach (double value in new[] { 0d, 100d, 200d })
        {
            slider.Value = value;
            var size = new Size(width, 30);
            host.Measure(size);
            host.Arrange(new Rect(size));
            slider.ApplyTemplate();
            host.UpdateLayout();
            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            track.Thumb.ApplyTemplate();
            var knob = Assert.IsType<Ellipse>(track.Thumb.Template.FindName("Knob", track.Thumb));
            var topLeft = knob.TranslatePoint(new Point(), slider);
            Assert.True(knob.ActualHeight >= 13 && knob.ActualWidth >= 13);
            Assert.InRange(topLeft.X, -0.1, slider.ActualWidth - knob.ActualWidth + 0.1);
            Assert.InRange(topLeft.Y, -0.1, slider.ActualHeight - knob.ActualHeight + 0.1);
            Assert.InRange(topLeft.Y + knob.ActualHeight / 2, slider.ActualHeight / 2 - 1, slider.ActualHeight / 2 + 1);
            Assert.Equal(value, track.Value);
        }
    });

    [Fact]
    public Task Library_player_preserves_previous_clip_or_uses_first_and_handles_empty() => OnSta(() =>
    {
        var temp = Directory.CreateTempSubdirectory("quickclip-player-navigation-");
        try
        {
            ClipItem Card(string name)
            {
                string path = System.IO.Path.Combine(temp.FullName, name + ".mp4");
                File.WriteAllText(path, "synthetic fixture; never decoded");
                return new ClipItem(new FileInfo(path), new ClipDetails(DateTimeOffset.UtcNow, "Fixture"), default);
            }
            var first = Card("first");
            var second = Card("second");
            var third = Card("third");
            var player = new PlayerView();
            player.Play([first, second], 1);
            Assert.Equal(second.Title, player.ClipTitle);
            player.Unload();
            player.PlayLibrary([third, second, first]);
            Assert.Equal(second.Title, player.ClipTitle);
            Assert.Equal(new[] { third.Path, second.Path, first.Path }, player.EditorNavigationPaths());
            Assert.True(player.HasPlaylist);
            player.Unload();
            player.PlayLibrary([third, first]);
            Assert.Equal(third.Title, player.ClipTitle);
            player.PlayLibrary([]);
            Assert.False(player.HasPlaylist);
            Assert.Null(player.ClipTitle);
        }
        finally { temp.Delete(recursive: true); }
    });

    private static async Task OnSta(Action body)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { body(); result.SetResult(); }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
