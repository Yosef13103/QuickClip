using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class WebmOverlayAnimationTests
{
    [Theory]
    [InlineData(495, 498, 10.1, 254, 256)]
    [InlineData(1920, 1080, 3.0, 256, 144)]
    [InlineData(64, 64, 1.0, 64, 64)]
    public void Frame_plan_bounds_resolution_and_rate(int width, int height, double duration, int outputWidth, int outputHeight)
    {
        var plan = Assert.IsType<WebmOverlayAnimation.Plan>(WebmOverlayAnimation.BuildPlan(width, height, duration));
        Assert.Equal(outputWidth, plan.Width);
        Assert.Equal(outputHeight, plan.Height);
        Assert.InRange(plan.Fps * duration, 1, WebmOverlayAnimation.MaxFrames);
        Assert.True(plan.FrameBytes <= 256 * 256 * 4);
    }

    [Theory]
    [InlineData(0, 16, 2)]
    [InlineData(16, 0, 2)]
    [InlineData(2049, 32, 2)]
    [InlineData(32, 32, 0)]
    [InlineData(32, 32, 31)]
    public void Invalid_or_excessive_video_is_rejected(int width, int height, double duration) =>
        Assert.Null(WebmOverlayAnimation.BuildPlan(width, height, duration));

    [Theory]
    [InlineData("{\"streams\":[{\"tags\":{\"ALPHA_MODE\":\"1\"}}]}", true)]
    [InlineData("{\"streams\":[{\"tags\":{\"alpha_mode\":\"1\"}}]}", true)]
    [InlineData("{\"streams\":[{\"tags\":{\"alpha_mode\":\"0\"}}]}", false)]
    [InlineData("{\"streams\":[{}]}", false)]
    public void Only_files_with_explicit_alpha_flag_are_supported(string json, bool expected) =>
        Assert.Equal(expected, WebmOverlayAnimation.HasAlphaTag(json));

    [Fact]
    public async Task Bundled_ffmpeg_decodes_a_real_alpha_webm_without_losing_transparency()
    {
        if (Deps.FFmpeg == null || Deps.FFprobe == null) return; // No optional native dependencies in a plain dotnet test run.
        var folder = Directory.CreateTempSubdirectory("qc-webm-alpha-");
        try
        {
            string input = Path.Combine(folder.FullName, "source.png");
            string output = Path.Combine(folder.FullName, "transparent.webm");
            int size = 16;
            var pixels = new byte[size * size * 4];
            for (int y = 4; y < 12; y++)
                for (int x = 4; x < 12; x++)
                {
                    int index = (y * size + x) * 4;
                    pixels[index] = 30; pixels[index + 1] = 200; pixels[index + 2] = 220; pixels[index + 3] = 255;
                }
            var source = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(source));
            using (var file = File.Create(input)) png.Save(file);

            var (exit, _, error) = await Proc.RunAsync(Deps.FFmpeg,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-loop", "1", "-framerate", "8",
                 "-i", input, "-frames:v", "8", "-c:v", "libvpx-vp9", "-pix_fmt", "yuva420p",
                 "-auto-alt-ref", "0", "-y", output]);
            Assert.True(exit == 0, error);
            var asset = await ClipOverlayImage.LoadAssetAsync(output);
            var animation = Assert.IsType<ClipOverlayImage.Asset>(asset);
            Assert.InRange(animation.Frames.Length, 2, WebmOverlayAnimation.MaxFrames);
            var first = animation.First;
            Assert.True(first.IsFrozen);
            var decoded = new byte[first.PixelWidth * first.PixelHeight * 4];
            first.CopyPixels(decoded, first.PixelWidth * 4, 0);
            Assert.Equal((byte)0, decoded[3]); // top-left is transparent, NOT opaque black
            int middle = (first.PixelHeight / 2 * first.PixelWidth + first.PixelWidth / 2) * 4 + 3;
            Assert.True(decoded[middle] > 128); // interior remains visible
        }
        finally { folder.Delete(recursive: true); }
    }
}
