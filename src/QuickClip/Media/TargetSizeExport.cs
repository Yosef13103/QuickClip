namespace QuickClip.Media;

/// <summary>One byte policy and verified execution path for capped shares and exports.</summary>
internal static class TargetSizeExport
{
    public const int BytesPerMB = 1_000_000;
    public const double MaximumMB = 4000;
    private const int MaxAttempts = 3;

    internal static bool IsValidLimit(double value) =>
        double.IsFinite(value) && value >= 1d / BytesPerMB && value <= MaximumMB;

    internal static long Ceiling(ExportRequest request, double limitMB)
    {
        if (!IsValidLimit(limitMB))
            throw new ExportException("Enter a positive size up to 4000 MB.");
        var source = request.Media;
        if (source.Size <= 0 || !double.IsFinite(source.Duration) || !double.IsFinite(request.Duration) ||
            source.Duration <= 0 || request.Duration <= 0.01)
            throw new ExportException("The selection has no measurable video size or duration.");

        // Without packet-level accounting, use a proportional estimate for a selection.
        // The estimate is also a hard upper bound on the bytes we will publish.
        double fraction = Math.Min(1, request.Duration / source.Duration);
        if (request.Crop is { } crop && source.Video is { Width: > 0, Height: > 0 } v)
        {
            double cropRatio = Math.Clamp(crop.W * (double)crop.H / (v.Width * (double)v.Height), 0, 1);
            double audioRatio = Math.Clamp(source.Audio.Sum(a => a.BitRate > 0 ? a.BitRate : 160_000L) *
                source.Duration / (8d * source.Size), 0, 1);
            fraction *= audioRatio + (1 - audioRatio) * cropRatio;
        }
        return (long)Math.Floor(Math.Min(limitMB * BytesPerMB, source.Size * fraction));
    }

    internal static ExportRequest Plan(ExportRequest request, double limitMB, bool adaptive)
    {
        if (request.Media.Video is not { } video || !request.Container.HasVideo || request.Container.IsGif ||
            request.Container.VideoFamilies.Length == 0 || request.Container.Key == "other" ||
            request.Container.AudioEncoder is "flac" or "alac" ||
            request.Container.AudioEncoder?.StartsWith("pcm_", StringComparison.Ordinal) == true)
            throw new ExportException("Size limits require a supported video format with compressed audio.");
        if (request.Fps > 0 && (video.Fps <= 0 || request.Fps > video.Fps + 0.01))
            throw new ExportException("Choose Original or a lower frame rate for a size-limited export.");
        long ceiling = Ceiling(request, limitMB);
        return Adapt(request with { Quality = "size", AdaptiveSize = adaptive, SizeCeilingBytes = ceiling,
            TargetSizeMB = ceiling / (double)BytesPerMB });
    }

    private static ExportRequest Adapt(ExportRequest plan)
    {
        if (!plan.AdaptiveSize || plan.VideoMode != VideoMode.Encode) return plan;
        var video = plan.Media.Video!;
        int width = plan.Crop?.W ?? video.Width;
        int height = plan.Crop?.H ?? video.Height;
        double fps = plan.Fps > 0 ? plan.Fps : video.Fps > 0 ? video.Fps : 30;
        double videoBps = VideoBudget(plan, ExportBuilder.EstimateAudioBitsPerSecond(plan));
        int side = ShareCopy.ShortSideFor(width, height, fps, videoBps);
        return side > 0 && (plan.MaxShortSide == 0 || plan.MaxShortSide > side)
            ? plan with { MaxShortSide = side } : plan;
    }

    internal static double VideoBudget(ExportRequest request, long audioBitsPerSecond) =>
        request.TargetSizeMB * BytesPerMB * 8 * 0.9 / request.Duration - audioBitsPerSecond;

    internal static async Task<long> RunAsync(ExportRequest plan, long ceiling, string? softwareFallback,
        IProgress<(int Attempt, double Progress)> progress, Action? validating, CancellationToken ct, MediaJobContext? job = null)
    {
        bool verified = false;
        try
        {
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                int attemptNumber = attempt;
                progress.Report((attempt, 0));
                var args = ExportBuilder.BuildArgs(plan);
                try
                {
                    async Task<bool> Encode()
                    {
                        await ExportRunner.RunAsync(args, plan.OutputPath, plan.Duration,
                            new ProgressCallback<double>(value => progress.Report((attemptNumber, value))), ct, deletePartial: false);
                        return true;
                    }
                    if (job != null && plan.VideoMode == VideoMode.Encode)
                        await job.EncodeAsync(plan.EncoderId, Encode, ct);
                    else await Encode();
                }
                catch (ExportException) when (softwareFallback != null && plan.VideoMode == VideoMode.Encode &&
                    plan.EncoderId != softwareFallback && attempt < MaxAttempts && !ct.IsCancellationRequested)
                {
                    plan = plan with { EncoderId = softwareFallback };
                    continue;
                }

                validating?.Invoke();
                long bytes = new FileInfo(plan.OutputPath).Length;
                if (bytes > 0 && bytes <= ceiling)
                {
                    await ValidateAsync(plan, ct);
                    ct.ThrowIfCancellationRequested();
                    verified = true;
                    return bytes;
                }
                if (attempt == MaxAttempts || bytes <= 0) break;
                plan = Adapt(plan with
                {
                    VideoMode = VideoMode.Encode,
                    TargetSizeMB = plan.VideoMode == VideoMode.Copy
                        ? plan.TargetSizeMB
                        : plan.TargetSizeMB * (ceiling / (double)bytes) * 0.95,
                });
            }
            throw new ExportException($"The export did not fit under {ceiling / (double)BytesPerMB:0.######} MB. Increase the cap or shorten the selection.");
        }
        finally
        {
            if (!verified)
                try { File.Delete(plan.OutputPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static async Task ValidateAsync(ExportRequest plan, CancellationToken ct)
    {
        var output = await MediaInfo.ProbeAsync(plan.OutputPath, ct, localOnly: true);
        var sourceVideo = plan.Media.Video;
        int expectedTracks = ExportBuilder.AudioOutputCount(plan);
        double fps = sourceVideo?.Fps is > 0 and var sourceFps ? sourceFps : 30;
        double tolerance = Math.Max(0.25, 3 / fps);
        if (output.Size <= 0 || Math.Abs(output.Duration - plan.Duration) > tolerance ||
            output.Audio.Count != expectedTracks ||
            (output.Video != null) != (sourceVideo != null && (plan.Container.HasVideo || plan.Container.IsGif)))
            throw new ExportException("The staged output did not match the selected duration or streams.");
        if (plan.Quality == "size" && output.Video is { } v && sourceVideo is { } source &&
            (v.Width > (plan.Crop?.W ?? source.Width) || v.Height > (plan.Crop?.H ?? source.Height) ||
             v.Fps > (plan.Fps > 0 ? plan.Fps : fps) + 0.5))
            throw new ExportException("The staged output exceeds the selected video dimensions or frame rate.");
    }
}

internal sealed class ProgressCallback<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
