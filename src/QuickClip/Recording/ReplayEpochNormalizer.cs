using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuickClip.Recording;

/// <summary>Experimental offline exporter for independently encoded replay epochs.
/// Never called from the live capture, dispatcher, hotkey or native packet worker.</summary>
internal enum ReplayEpochGapPolicy { Reject, OmitMissingFootage }

// Hardware encoder IDs must have been capability-probed before calling ExportAsync.
// Unspecified uses the source codec's software encoder, never silently converts AV1
// or HEVC to H.264. Live saving must independently gate heavy transcoding.
internal sealed record ReplayEpochEncodingOptions(string? EncoderId = null);

internal static class ReplayEpochNormalizer
{
    internal sealed record Media(Guid EpochId, string Path);
    internal sealed record Result(string Path, int Width, int Height, int AudioTracks, TimeSpan Duration,
        TimeSpan OmittedMissingFootage, bool StreamCopied = false)
    {
        internal string VideoCodec { get; init; } = "";
    }

    private sealed record Probe(int Width, int Height, double DurationSeconds, string[] AudioTitles,
        double FramesPerSecond, string VideoCodec, string PixelFormat, string ColorTransfer,
        string ColorPrimaries, string ColorSpace);

    internal static async Task<Result> ExportAsync(
        ReplayEpochExportPlan plan, IReadOnlyList<Media> media, string destination,
        string ffmpegPath, string ffprobePath, int fps = 30,
        ReplayEpochGapPolicy gapPolicy = ReplayEpochGapPolicy.Reject,
        CancellationToken cancellationToken = default,
        ReplayEpochOutputOptions? outputOptions = null,
        ReplayEpochEncodingOptions? encodingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        if (plan.Slices.Count is < 1 or > 32 ||
            plan.HasDiscontinuities && gapPolicy != ReplayEpochGapPolicy.OmitMissingFootage ||
            fps is < 1 or > 240)
            throw new InvalidOperationException("Unsupported epoch export plan. Discontinuities require an explicit gap policy.");
        var (outputWidth, outputHeight) = ReplayEpochCanvas.Resolve(plan, outputOptions);

        string target = Path.GetFullPath(destination);
        string extension = Path.GetExtension(target).ToLowerInvariant();
        if (extension is not (".mp4" or ".mkv") ||
            !Directory.Exists(Path.GetDirectoryName(target)) || File.Exists(target))
            throw new IOException("Choose a new MP4 or MKV destination in an existing folder.");
        if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            throw new FileNotFoundException("The bundled FFmpeg and FFprobe tools are required.");

        var sources = media.ToDictionary(item => item.EpochId);
        var probes = new List<Probe>(plan.Slices.Count);
        var paths = new List<string>(plan.Slices.Count);
        string?[]? firstTitles = null;
        double duration = 0;
        foreach (var slice in plan.Slices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!sources.TryGetValue(slice.Epoch.EpochId, out var source))
                throw new IOException("An encoded epoch segment is missing.");
            string path = Path.GetFullPath(source.Path);
            if (!File.Exists(path) || string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
                throw new IOException("An encoded epoch input is missing or overlaps the output.");
            var probe = await ProbeAsync(ffprobePath, path, cancellationToken).ConfigureAwait(false);
            if (probe.Width != slice.Epoch.Width || probe.Height != slice.Epoch.Height)
                throw new InvalidOperationException("The segment's actual video dimensions disagree with its immutable epoch metadata.");
            double from = (slice.StartUs - slice.Epoch.StartUs) / 1_000_000.0;
            double length = (slice.EndUs - slice.StartUs) / 1_000_000.0;
            if (from < 0 || length <= 0 || from + length > probe.DurationSeconds + 0.1)
                throw new InvalidOperationException("An epoch slice falls outside the available source file.");
            if (firstTitles == null)
                firstTitles = probe.AudioTitles;
            else if (!firstTitles.SequenceEqual(probe.AudioTitles, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("The audio track layout changed between epochs; explicit track mapping is required.");
            duration += length;
            if (duration > 15 * 60)
                throw new InvalidOperationException("Experimental epoch exports are limited to 15 minutes.");
            probes.Add(probe);
            paths.Add(path);
        }

        int tracks = firstTitles?.Length ?? 0;
        if (tracks > 16) throw new InvalidOperationException("Too many audio tracks.");
        // A complete unchanged epoch is container work, not video transcoding.
        // A requested encoder override always disables stream copy.
        bool canStreamCopy = encodingOptions?.EncoderId == null &&
            plan.Slices.Count == 1 && !plan.HasDiscontinuities &&
            !plan.RequiresVideoNormalization &&
            plan.Slices[0].StartUs == plan.Slices[0].Epoch.StartUs &&
            plan.Slices[0].EndUs == plan.Slices[0].Epoch.EndUs &&
            probes[0].Width == outputWidth && probes[0].Height == outputHeight &&
            Math.Abs(probes[0].FramesPerSecond - fps) < 0.01;
        // Do not collapse HDR into 8-bit SDR without an explicit, tested tonemap.
        bool hdr = probes.Any(p => p.ColorTransfer is "smpte2084" or "arib-std-b67");
        if (hdr && probes.Any(p => p.ColorTransfer != probes[0].ColorTransfer ||
            p.ColorPrimaries != probes[0].ColorPrimaries || p.ColorSpace != probes[0].ColorSpace))
            throw new InvalidOperationException("Mixed HDR/SDR or incompatible color metadata requires an explicit color conversion policy.");

        string outputCodec = probes[^1].VideoCodec; // Most recent epoch for a mixed-codec interval.
        string encoderId = encodingOptions?.EncoderId ?? outputCodec switch
        {
            "av1" => "libsvtav1",
            "hevc" => "libx265",
            "h264" => "libx264",
            _ when canStreamCopy => "",
            _ => throw new InvalidOperationException("No safe default encoder for this video codec.")
        };
        string requestedCodec = canStreamCopy ? outputCodec : encoderId switch
        {
            "av1_nvenc" or "av1_amf" or "av1_qsv" or "libsvtav1" => "av1",
            "hevc_nvenc" or "hevc_amf" or "hevc_qsv" or "libx265" => "hevc",
            "h264_nvenc" or "h264_amf" or "h264_qsv" or "libx264" => "h264",
            _ => throw new ArgumentException("Unsupported epoch normalization encoder.", nameof(encodingOptions))
        };
        bool tenBit = hdr || probes.Any(p =>
            p.PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase));
        if (!canStreamCopy && probes.Any(p =>
            p.PixelFormat.Contains("12", StringComparison.OrdinalIgnoreCase) ||
            p.PixelFormat.Contains("16", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("High bit-depth epoch normalization needs an explicit color-depth policy.");
        if (!canStreamCopy && tenBit && requestedCodec == "h264")
            throw new InvalidOperationException("10-bit/HDR epoch normalization requires HEVC or AV1.");

        string pixelFormat = tenBit ? "yuv420p10le" : "yuv420p";
        var filters = new StringBuilder();
        static string T(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        for (int i = 0; i < plan.Slices.Count; i++)
        {
            var slice = plan.Slices[i];
            double from = (slice.StartUs - slice.Epoch.StartUs) / 1_000_000.0;
            double length = (slice.EndUs - slice.StartUs) / 1_000_000.0;
            filters.Append('[').Append(i).Append(":v:0]trim=start=").Append(T(from))
                .Append(":duration=").Append(T(length)).Append(",setpts=PTS-STARTPTS,fps=")
                .Append(fps).Append(",scale=").Append(outputWidth).Append(':').Append(outputHeight)
                .Append(":force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic")
                .Append(",pad=").Append(outputWidth).Append(':').Append(outputHeight)
                .Append(":(ow-iw)/2:(oh-ih)/2,setsar=1,format=").Append(pixelFormat)
                .Append("[v").Append(i).Append("];");
            for (int track = 0; track < tracks; track++)
            {
                filters.Append('[').Append(i).Append(":a:").Append(track)
                    .Append("]atrim=start=").Append(T(from)).Append(":duration=").Append(T(length))
                    .Append(",asetpts=PTS-STARTPTS,aresample=48000:async=1:first_pts=0")
                    .Append(",aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=duration=")
                    .Append(T(length)).Append(",asetpts=PTS-STARTPTS[a")
                    .Append(i).Append('_').Append(track).Append("];");
            }
        }
        for (int i = 0; i < plan.Slices.Count; i++) filters.Append("[v").Append(i).Append(']');
        if (plan.Slices.Count == 1) filters.Append("null[v];");
        else filters.Append("concat=n=").Append(plan.Slices.Count).Append(":v=1:a=0[v];");

        for (int track = 0; track < tracks; track++)
        {
            for (int i = 0; i < plan.Slices.Count; i++)
                filters.Append("[a").Append(i).Append('_').Append(track).Append(']');
            if (plan.Slices.Count == 1) filters.Append("anull[a").Append(track).Append("];");
            else filters.Append("concat=n=").Append(plan.Slices.Count)
                .Append(":v=0:a=1[a").Append(track).Append("];");
        }
        string graph = filters.ToString().TrimEnd(';');
        double omittedSeconds = plan.Gaps.Sum(gap => (gap.EndUs - gap.StartUs) / 1_000_000.0);

        string staging = Path.Combine(Path.GetDirectoryName(target)!,
            ".quickclip-tmp-epoch-" + Guid.NewGuid().ToString("N") + ".partial" + extension);
        // Multiple epochs always normalize until packet/timestamp compatibility is proven.
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-n" };
        foreach (string path in paths) { args.Add("-i"); args.Add(path); }
        if (canStreamCopy)
            args.AddRange(["-map", "0", "-c", "copy"]);
        else
        {
            args.AddRange(["-filter_complex", graph, "-map", "[v]"]);
            for (int track = 0; track < tracks; track++) args.AddRange(["-map", "[a" + track + "]"]);
            args.AddRange(["-c:v", encoderId, "-threads", "2", "-pix_fmt", pixelFormat,
                "-r", fps.ToString(CultureInfo.InvariantCulture), "-c:a", "aac", "-b:a", "160k"]);
            if (encoderId.EndsWith("_nvenc", StringComparison.Ordinal))
                args.AddRange(["-preset", "p2", "-tune", "hq", "-cq", "22", "-b:v", "0"]);
            else if (encoderId.EndsWith("_amf", StringComparison.Ordinal))
                args.AddRange(["-quality", "speed"]);
            else if (encoderId == "libx264")
                args.AddRange(["-preset", "veryfast", "-crf", "19"]);
            else if (encoderId == "libx265")
                args.AddRange(["-preset", "fast", "-crf", "22"]);
            else if (encoderId == "libsvtav1")
                // FFmpeg's -threads alone does not bound SVT's internal worker pool.
                // The bundled SVT-AV1 4.3 probe accepted lp=2 and reported two workers.
                args.AddRange(["-preset", "9", "-crf", "26", "-svtav1-params", "lp=2"]);
            if (hdr)
                args.AddRange(["-color_trc", probes[0].ColorTransfer,
                    "-color_primaries", probes[0].ColorPrimaries, "-colorspace", probes[0].ColorSpace]);
        }
        if (extension == ".mp4") args.AddRange(["-movflags", "+faststart"]);
        // A .partial extension alone is ambiguous; explicitly choose the muxer.
        args.AddRange(["-f", extension == ".mp4" ? "mp4" : "matroska"]);
        for (int track = 0; track < tracks; track++)
            args.AddRange(["-metadata:s:a:" + track, "title=" + firstTitles![track],
                "-metadata:s:a:" + track, "handler_name=" + firstTitles[track]]);
        if (omittedSeconds > 0)
            args.AddRange(["-metadata", "comment=QuickClip replay: " + T(omittedSeconds) +
                " seconds of unavailable source footage omitted."]);
        args.Add(staging);
        try
        {
            await RunAsync(ffmpegPath, args, cancellationToken,
                reducedPriority: !canStreamCopy).ConfigureAwait(false);
            var result = await ProbeAsync(ffprobePath, staging, cancellationToken).ConfigureAwait(false);
            if (result.Width != outputWidth || result.Height != outputHeight ||
                !result.AudioTitles.SequenceEqual(firstTitles!, StringComparer.OrdinalIgnoreCase) ||
                Math.Abs(result.DurationSeconds - duration) > Math.Max(0.15, plan.Slices.Count / (double)fps) ||
                result.VideoCodec != (canStreamCopy ? probes[0].VideoCodec : requestedCodec) ||
                hdr && (result.ColorTransfer != probes[0].ColorTransfer ||
                    result.ColorPrimaries != probes[0].ColorPrimaries ||
                    result.ColorSpace != probes[0].ColorSpace))
                throw new IOException("The normalized output did not pass codec/color/video/audio/duration validation.");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target)) throw new IOException("The destination was created concurrently.");
            File.Move(staging, target);
            return new Result(target, outputWidth, outputHeight, tracks,
                TimeSpan.FromSeconds(result.DurationSeconds), TimeSpan.FromSeconds(omittedSeconds),
                StreamCopied: canStreamCopy) { VideoCodec = result.VideoCodec };
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    // Container-only conversion used by the Editor's "Convert to MP4" action.
    // Does not resize or re-encode streams. Incompatible streams fail rather than fall back
    // to lossy conversion or silently discard independent audio tracks.
    internal static async Task<Result> RemuxMkvToMp4Async(
        string sourcePath, string destination, string ffmpegPath, string ffprobePath,
        CancellationToken cancellationToken = default)
    {
        string input = Path.GetFullPath(sourcePath);
        string target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(input), ".mkv", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(target), ".mp4", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(input, target, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(input) || !Directory.Exists(Path.GetDirectoryName(target)) ||
            File.Exists(target))
            throw new IOException("Choose an existing MKV and a new MP4 destination.");
        if (!File.Exists(ffmpegPath) || !File.Exists(ffprobePath))
            throw new FileNotFoundException("The bundled FFmpeg tools are required.");

        var before = await ProbeAsync(ffprobePath, input, cancellationToken).ConfigureAwait(false);
        if (before.AudioTitles.Length > 16)
            throw new InvalidOperationException("Too many audio streams for an epoch remux.");

        string staging = Path.Combine(Path.GetDirectoryName(target)!,
            ".quickclip-tmp-remux-" + Guid.NewGuid().ToString("N") + ".partial.mp4");
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error",
            "-n", "-i", input, "-map", "0", "-c", "copy",
            "-movflags", "+faststart", "-f", "mp4" };
        for (int i = 0; i < before.AudioTitles.Length; i++)
            args.AddRange(["-metadata:s:a:" + i, "title=" + before.AudioTitles[i],
                "-metadata:s:a:" + i, "handler_name=" + before.AudioTitles[i]]);
        args.Add(staging);

        try
        {
            await RunAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
            var after = await ProbeAsync(ffprobePath, staging, cancellationToken).ConfigureAwait(false);
            if (before.Width != after.Width || before.Height != after.Height ||
                before.VideoCodec != after.VideoCodec || before.PixelFormat != after.PixelFormat ||
                before.ColorTransfer != after.ColorTransfer ||
                before.ColorPrimaries != after.ColorPrimaries || before.ColorSpace != after.ColorSpace ||
                !before.AudioTitles.SequenceEqual(after.AudioTitles, StringComparer.OrdinalIgnoreCase) ||
                Math.Abs(before.DurationSeconds - after.DurationSeconds) > 0.15)
                throw new IOException("The stream-copy remux changed dimensions, audio tracks or duration.");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target)) throw new IOException("The destination was created concurrently.");
            File.Move(staging, target);
            return new Result(target, after.Width, after.Height, after.AudioTitles.Length,
                TimeSpan.FromSeconds(after.DurationSeconds), TimeSpan.Zero, StreamCopied: true)
                { VideoCodec = after.VideoCodec };
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static async Task<Probe> ProbeAsync(string ffprobe, string path, CancellationToken cancel)
    {
        string output = await RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt,color_transfer,color_primaries,color_space,width,height,avg_frame_rate:stream_tags=title,handler_name:format=duration",
                "-of", "json", path], cancel).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var videos = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => item.GetProperty("codec_type").GetString() == "video").ToArray();
        if (videos.Length != 1) throw new InvalidOperationException("Expected exactly one video stream.");
        var audio = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => item.GetProperty("codec_type").GetString() == "audio").ToArray();
        string[] names = audio.Select((item, i) =>
        {
            if (item.TryGetProperty("tags", out var tags))
            {
                if (tags.TryGetProperty("title", out var title) &&
                    !string.IsNullOrWhiteSpace(title.GetString()))
                    return title.GetString()!;
                // MP4 commonly persists handler_name but discards Matroska's
                // free-form title. Rejecting renamed/misordered stems requires both.
                if (tags.TryGetProperty("handler_name", out var handler) &&
                    !string.IsNullOrWhiteSpace(handler.GetString()) &&
                    handler.GetString() is not ("SoundHandler" or "VideoHandler"))
                    return handler.GetString()!;
            }
            return "Audio " + (i + 1);
        }).ToArray();
        double seconds = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
        string[] rate = videos[0].GetProperty("avg_frame_rate").GetString()!.Split('/');
        double fps = rate.Length == 2 &&
            double.TryParse(rate[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
            double.TryParse(rate[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double den) &&
            den > 0 ? n / den : 0;
        static string Read(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";
        return new Probe(videos[0].GetProperty("width").GetInt32(),
            videos[0].GetProperty("height").GetInt32(), seconds, names, fps,
            Read(videos[0], "codec_name"), Read(videos[0], "pix_fmt"),
            Read(videos[0], "color_transfer"), Read(videos[0], "color_primaries"),
            Read(videos[0], "color_space"));
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> args,
        CancellationToken cancellationToken, bool reducedPriority = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(reducedPriority ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Couldn't start bundled FFmpeg.");
        if (reducedPriority)
        {
            // Heavy normalization must not contend at normal priority with active games.
            // Priority changes can fail under restricted tokens; the export still runs.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string stdout = await output.ConfigureAwait(false);
            string stderr = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("FFmpeg operation failed: " + stderr[..Math.Min(stderr.Length, 1200)]);
            return stdout;
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
