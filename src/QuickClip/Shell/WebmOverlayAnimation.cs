using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickClip.Media;

namespace QuickClip.Shell;

/// <summary>
/// Decodes short, local VP8/VP9 WebM alpha animations into bounded, frozen WPF frames.
/// FFmpeg's native VP8/VP9 decoders drop WebM BlockAdditional alpha silently:
/// the bundled libvpx decoder MUST be selected before -i.
/// </summary>
internal static class WebmOverlayAnimation
{
    internal const double MaxDurationSeconds = 30;
    internal const int MaxFrames = 120;
    internal const int MaxOutputDimension = 256;
    private static readonly TimeSpan DecodeTimeout = TimeSpan.FromSeconds(25);

    internal sealed record Plan(int Width, int Height, int Fps)
    {
        internal int FrameBytes => checked(Width * Height * 4);
        internal TimeSpan FrameDelay => TimeSpan.FromSeconds(1d / Fps);
    }

    internal static Plan? BuildPlan(int width, int height, double duration)
    {
        if (width is < 1 or > ClipOverlayImage.MaxDimension ||
            height is < 1 or > ClipOverlayImage.MaxDimension ||
            !double.IsFinite(duration) || duration <= 0 || duration > MaxDurationSeconds)
            return null;
        double scale = Math.Min(1, MaxOutputDimension / (double)Math.Max(width, height));
        int w = Math.Max(1, (int)Math.Round(width * scale));
        int h = Math.Max(1, (int)Math.Round(height * scale));
        int fps = Math.Clamp((int)Math.Floor(MaxFrames / duration), 1, 15);
        return new(w, h, fps);
    }

    internal static bool HasAlphaTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array) return false;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) continue;
                foreach (var tag in tags.EnumerateObject())
                    if (string.Equals(tag.Name, "alpha_mode", StringComparison.OrdinalIgnoreCase) &&
                        tag.Value.ToString() == "1") return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    internal static async Task<ClipOverlayImage.Asset?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Deps.FFmpeg == null || Deps.FFprobe == null || !File.Exists(path)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DecodeTimeout);
        try
        {
            var file = new FileInfo(path);
            if (file.Length is < 1 or > ClipOverlayImage.MaxBytes) return null;
            var media = await MediaInfo.ProbeAsync(path, timeout.Token, localOnly: true);
            if (media.Video is not { } video || BuildPlan(video.Width, video.Height, media.Duration) is not { } plan)
                return null;

            string decoder = video.Codec switch
            {
                "vp8" => "libvpx",
                "vp9" => "libvpx-vp9",
                _ => "",
            };
            if (decoder.Length == 0) return null;

            // Requiring alpha_mode prevents an opaque WebM from being mistaken for a transparent animation.
            var (probeCode, probeOutput, _) = await Proc.RunAsync(Deps.FFprobe,
                ["-v", "error", "-select_streams", "v:0", "-show_entries",
                 "stream_tags=alpha_mode", "-of", "json", "-protocol_whitelist", "file", path], timeout.Token);
            if (probeCode != 0 || !HasAlphaTag(probeOutput)) return null;

            string fps = plan.Fps.ToString(CultureInfo.InvariantCulture);
            var args = new[]
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-protocol_whitelist", "file",
                "-c:v", decoder, "-i", path,
                "-map", "0:v:0", "-an", "-sn", "-dn",
                "-vf", $"fps={fps},scale={plan.Width}:{plan.Height}:flags=bicubic,format=bgra",
                "-frames:v", MaxFrames.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
            };
            using var process = new Process { StartInfo = Proc.StartInfo(Deps.FFmpeg, args) };
            if (!process.Start()) return null;
            using var stop = timeout.Token.Register(() => Proc.KillQuietly(process));
            Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var frames = new List<ClipOverlayImage.Frame>(MaxFrames);
            var stream = process.StandardOutput.BaseStream;
            int frameBytes = plan.FrameBytes;
            while (frames.Count < MaxFrames)
            {
                var pixels = new byte[frameBytes];
                int filled = 0;
                while (filled < pixels.Length)
                {
                    int count = await stream.ReadAsync(pixels.AsMemory(filled), timeout.Token);
                    if (count == 0) break;
                    filled += count;
                }
                if (filled == 0) break;
                if (filled != frameBytes) return null;
                var bitmap = BitmapSource.Create(plan.Width, plan.Height, 96, 96,
                    PixelFormats.Bgra32, null, pixels, plan.Width * 4);
                bitmap.Freeze();
                frames.Add(new(bitmap, plan.FrameDelay));
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || frames.Count == 0)
            {
                Log.Write("WebM overlay decode failed: " + (await stderr).Split('\n').FirstOrDefault());
                return null;
            }
            return new([.. frames]);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or Win32Exception or OverflowException or JsonException)
        {
            Log.Write("WebM overlay unavailable: " + ex.GetType().Name);
            return null;
        }
    }
}
