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
    public Task Shared_action_buttons_are_square_centered_and_scale_with_controls() => OnSta(() =>
    {
        var host = new StackPanel();
        host.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/QuickClip;component/Themes/Styles.xaml", UriKind.Relative)
        });
        host.Resources.MergedDictionaries.Add(new ResourceDictionary());
        var theme = host.Resources.MergedDictionaries[1];
        new ThemeResources().Apply(theme, ThemeChoice.Amoled);
        var dot = new Ellipse { Width = 19, Height = 19, Fill = System.Windows.Media.Brushes.Orange };
        var glyph = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16, Stretch = System.Windows.Media.Stretch.Uniform,
            Fill = System.Windows.Media.Brushes.White,
            Data = System.Windows.Media.Geometry.Parse("M0,0 L16,8 L0,16 Z")
        };
        var icon = new Button { Style = (Style)host.FindResource("IconButton"), Content = glyph, ToolTip = "Play" };
        var swatch = new Button { Style = (Style)host.FindResource("ColorSwatchButton"), Content = dot, ToolTip = "Change color" };
        var text = new Button { Style = (Style)host.FindResource("TextButton"), Content = "Set in" };
        var plain = new Button { Content = "OK" };
        var toggle = new ToggleButton { Style = (Style)host.FindResource("EditorOptionToggle"), Content = "Solo" };
        foreach (var control in new Control[] { icon, swatch, text, plain, toggle })
        {
            control.HorizontalAlignment = HorizontalAlignment.Left;
            host.Children.Add(control);
        }
        foreach (int scale in new[] { 90, 100, 125 })
        {
            new InterfaceScale(110, scale).Apply(host.Resources);
            host.Measure(new Size(500, 500));
            host.Arrange(new Rect(0, 0, 500, 500));
            host.UpdateLayout();
            double minimum = 36 * scale / 100d;
            foreach (var control in new Control[] { icon, swatch, text, plain, toggle })
            {
                Assert.True(control.ActualWidth >= minimum - .1, $"{control.GetType().Name} width {control.ActualWidth}");
                Assert.True(control.ActualHeight >= minimum - .1, $"Height {control.ActualHeight}");
            }
            foreach (var (button, content) in new[] { (icon, (FrameworkElement)glyph), (swatch, (FrameworkElement)dot) })
            {
                Assert.Equal(minimum, button.ActualWidth, 1);
                Assert.Equal(minimum, button.ActualHeight, 1);
                var center = content.TranslatePoint(new Point(content.ActualWidth / 2, content.ActualHeight / 2), button);
                Assert.InRange(center.X, minimum / 2 - .6, minimum / 2 + .6);
                Assert.InRange(center.Y, minimum / 2 - .6, minimum / 2 + .6);
            }
            swatch.ApplyTemplate();
            var surface = Assert.IsType<Border>(swatch.Template.FindName("Surface", swatch));
            Assert.Equal(System.Windows.Media.Colors.Transparent, Assert.IsType<System.Windows.Media.SolidColorBrush>(surface.Background).Color);
            Assert.Equal(System.Windows.Media.Colors.Transparent, Assert.IsType<System.Windows.Media.SolidColorBrush>(surface.BorderBrush).Color);
            Assert.NotNull(icon.ToolTip);
            Assert.NotNull(swatch.ToolTip);
        }
    });

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
