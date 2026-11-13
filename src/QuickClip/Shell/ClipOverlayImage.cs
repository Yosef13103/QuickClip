using System.IO;
using System.Windows.Media.Imaging;

namespace QuickClip.Shell;

internal static class ClipOverlayImage
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal const int MaxDimension = 2048;
    internal const int MaxGifFrames = 120;
    internal const long MaxGifSourceFramePixels = 32L * 1024 * 1024;

    internal static bool WithinGifBudget(int width, int height, int frames) =>
        width is >= 1 and <= MaxDimension && height is >= 1 and <= MaxDimension &&
        frames is >= 1 and <= MaxGifFrames &&
        (long)width * height * frames <= MaxGifSourceFramePixels;

    internal sealed record Frame(BitmapSource Bitmap, TimeSpan Delay);
    internal sealed record Asset(Frame[] Frames)
    {
        internal BitmapSource First => Frames[0].Bitmap;
    }

    internal static BitmapSource? Load(string? path) => LoadAsset(path)?.First;

    internal static Asset? LoadAsset(string? path)
    {
        if (string.IsNullOrEmpty(path) || Discovery.CatalogPaths.LocalPath(path) == null) return null;
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length is < 1 or > MaxBytes) return null;
            // A fixed-size snapshot bounds reads even if another process changes the file.
            var data = new byte[(int)file.Length];
            file.ReadExactly(data);
            using var stream = new MemoryStream(data, writable: false);
            BitmapDecoder decoder = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => new PngBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None),
                ".jpg" or ".jpeg" => new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None),
                ".gif" => new GifBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None),
                ".webp" when data.AsSpan().StartsWith("RIFF"u8) && data.Length >= 12 &&
                    data.AsSpan(8, 4).SequenceEqual("WEBP"u8) => BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None),
                _ => throw new NotSupportedException(),
            };
            if (decoder is GifBitmapDecoder gif) return DecodeGif(gif, data);
            if (decoder.Frames.Count != 1) return null;
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is < 1 or > MaxDimension || frame.PixelHeight is < 1 or > MaxDimension) return null;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            if (frame.PixelWidth >= frame.PixelHeight) image.DecodePixelWidth = Math.Min(frame.PixelWidth, 128);
            else image.DecodePixelHeight = Math.Min(frame.PixelHeight, 128);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new([new(image, TimeSpan.Zero)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or FormatException or InvalidOperationException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static Asset? DecodeGif(GifBitmapDecoder decoder, byte[] data)
    {
        if (data.Length < 13) return null;
        int width = data[6] | data[7] << 8;
        int height = data[8] | data[9] << 8;
        // Budget source frame compositing separately from the bounded 256px output frame cache.
        // The owner's 495x498, 101-frame GIF needs about 24.9M source frame-pixels.
        if (!WithinGifBudget(width, height, decoder.Frames.Count)) return null;

        int stride = width * 4;
        var canvas = new byte[stride * height];
        var previous = new byte[canvas.Length];
        var frames = new List<Frame>(decoder.Frames.Count);
        double scale = Math.Min(1, 256d / Math.Max(width, height));
        foreach (var frame in decoder.Frames)
        {
            var metadata = frame.Metadata as BitmapMetadata;
            int left = Read(metadata, "/imgdesc/Left"), top = Read(metadata, "/imgdesc/Top");
            int disposal = Read(metadata, "/grctlext/Disposal");
            int w = frame.PixelWidth, h = frame.PixelHeight;
            if (w < 1 || h < 1 || left + w > width || top + h > height) return null;
            if (disposal == 3) canvas.CopyTo(previous, 0);

            var bgra = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            var patch = new byte[w * h * 4];
            bgra.CopyPixels(patch, w * 4, 0);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int source = (y * w + x) * 4;
                    if (patch[source + 3] == 0) continue;
                    int target = (top + y) * stride + (left + x) * 4;
                    Buffer.BlockCopy(patch, source, canvas, target, 4);
                }

            var full = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, canvas, stride);
            BitmapSource scaled = scale < 1 ? new TransformedBitmap(full, new System.Windows.Media.ScaleTransform(scale, scale)) : full;
            // Materialize only the small display frame. Keeping the transform would retain the large canvas.
            var pixels = new byte[scaled.PixelWidth * scaled.PixelHeight * 4];
            scaled.CopyPixels(pixels, scaled.PixelWidth * 4, 0);
            var display = BitmapSource.Create(scaled.PixelWidth, scaled.PixelHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, scaled.PixelWidth * 4);
            display.Freeze();
            int delay = Read(metadata, "/grctlext/Delay");
            frames.Add(new(display, TimeSpan.FromMilliseconds(delay < 2 ? 100 : delay * 10)));

            if (disposal == 3) (canvas, previous) = (previous, canvas);
            else if (disposal == 2)
                for (int y = 0; y < h; y++) Array.Clear(canvas, (top + y) * stride + left * 4, w * 4);
        }
        return new([.. frames]);
    }

    private static int Read(BitmapMetadata? metadata, string key) =>
        metadata?.ContainsQuery(key) == true ? Convert.ToInt32(metadata.GetQuery(key)) : 0;
}
