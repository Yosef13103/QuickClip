using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace QuickClip.Recording;

/// <summary>Experimental offline exporter for independently encoded replay epochs.
/// Never called from the live capture, dispatcher, hotkey or native packet worker.</summary>
internal enum ReplayEpochGapPolicy { Reject, OmitMissingFootage }

internal static class ReplayEpochNormalizer
{
    internal sealed record Media(Guid EpochId, string Path);
    internal sealed record Result(string Path, int Width, int Height, int AudioTracks, TimeSpan Duration,
        TimeSpan OmittedMissingFootage);

    private sealed record Probe(int Width, int Height, double DurationSeconds, string[] AudioTitles);

    internal static async Task<Result> ExportAsync(
        ReplayEpochExportPlan plan, IReadOnlyList<Media> media, string destination,
        string ffmpegPath, string ffprobePath, int fps = 30,
        ReplayEpochGapPolicy gapPolicy = ReplayEpochGapPolicy.Reject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(media);
        if (plan.Slices.Count is < 1 or > 32 ||
            plan.HasDiscontinuities && gapPolicy != ReplayEpochGapPolicy.OmitMissingFootage ||
            plan.Width is < 16 or > 8192 || plan.Height is < 16 or > 4320 ||
            (plan.Width & 1) != 0 || (plan.Height & 1) != 0 ||
            fps is < 1 or > 240)
            throw new InvalidOperationException("Unsupported epoch export plan. Discontinuities require an explicit gap policy.");

        string target = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(target), ".mp4", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(Path.GetDirectoryName(target)) || File.Exists(target))
            throw new IOException("Choose a new MP4 destination in an existing folder.");
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
        var filters = new StringBuilder();
        static string T(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        for (int i = 0; i < plan.Slices.Count; i++)
        {
            var slice = plan.Slices[i];
            double from = (slice.StartUs - slice.Epoch.StartUs) / 1_000_000.0;
            double length = (slice.EndUs - slice.StartUs) / 1_000_000.0;
            filters.Append('[').Append(i).Append(":v:0]trim=start=").Append(T(from))
                .Append(":duration=").Append(T(length)).Append(",setpts=PTS-STARTPTS,fps=")
                .Append(fps).Append(",scale=").Append(plan.Width).Append(':').Append(plan.Height)
                .Append(":force_original_aspect_ratio=decrease:force_divisible_by=2:flags=bicubic")
                .Append(",pad=").Append(plan.Width).Append(':').Append(plan.Height)
                .Append(":(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[v").Append(i).Append("];");
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
            ".quickclip-epoch-" + Guid.NewGuid().ToString("N") + ".partial.mp4");
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-n" };
        foreach (string path in paths) { args.Add("-i"); args.Add(path); }
        args.AddRange(["-filter_complex", graph, "-map", "[v]"]);
        for (int track = 0; track < tracks; track++) args.AddRange(["-map", "[a" + track + "]"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-threads", "2", "-crf", "19",
            "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart"]);
        for (int track = 0; track < tracks; track++)
            args.AddRange(["-metadata:s:a:" + track, "title=" + firstTitles![track]]);
        if (omittedSeconds > 0)
            args.AddRange(["-metadata", "comment=QuickClip replay: " + T(omittedSeconds) +
                " seconds of unavailable source footage omitted."]);
        args.Add(staging);
        try
        {
            await RunAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
            var result = await ProbeAsync(ffprobePath, staging, cancellationToken).ConfigureAwait(false);
            if (result.Width != plan.Width || result.Height != plan.Height ||
                result.AudioTitles.Length != tracks ||
                Math.Abs(result.DurationSeconds - duration) > Math.Max(0.15, plan.Slices.Count / (double)fps))
                throw new IOException("The normalized output did not pass video/audio/duration validation.");
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(target)) throw new IOException("The destination was created concurrently.");
            File.Move(staging, target);
            return new Result(target, plan.Width, plan.Height, tracks,
                TimeSpan.FromSeconds(result.DurationSeconds), TimeSpan.FromSeconds(omittedSeconds));
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private static async Task<Probe> ProbeAsync(string ffprobe, string path, CancellationToken cancel)
    {
        string output = await RunAsync(ffprobe,
            ["-v", "error", "-show_entries", "stream=codec_type,width,height:stream_tags=title:format=duration",
                "-of", "json", path], cancel).ConfigureAwait(false);
        using var document = JsonDocument.Parse(output);
        var videos = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => item.GetProperty("codec_type").GetString() == "video").ToArray();
        if (videos.Length != 1) throw new InvalidOperationException("Expected exactly one video stream.");
        var audio = document.RootElement.GetProperty("streams").EnumerateArray()
            .Where(item => item.GetProperty("codec_type").GetString() == "audio").ToArray();
        string[] names = audio.Select((item, i) => item.TryGetProperty("tags", out var tags) &&
            tags.TryGetProperty("title", out var title) && !string.IsNullOrWhiteSpace(title.GetString())
                ? title.GetString()! : "Audio " + (i + 1)).ToArray();
        double seconds = double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
            CultureInfo.InvariantCulture);
        return new Probe(videos[0].GetProperty("width").GetInt32(),
            videos[0].GetProperty("height").GetInt32(), seconds, names);
    }

    private static async Task<string> RunAsync(string executable, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Couldn't start bundled FFmpeg.");
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
