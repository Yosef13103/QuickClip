using System.IO;
using System.Windows.Media.Imaging;

namespace QuickClip.Shell;

internal static class ClipOverlayImage
{
    internal const int MaxBytes = 4 * 1024 * 1024;
    internal const int MaxDimension = 2048;

    internal static BitmapSource? Load(string? path)
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
                _ => throw new NotSupportedException(),
            };
            if (decoder.Frames.Count != 1) return null;
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is < 1 or > MaxDimension || frame.PixelHeight is < 1 or > MaxDimension) return null;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Min(frame.PixelWidth, 128);
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or FormatException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
