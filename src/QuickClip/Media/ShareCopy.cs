namespace QuickClip.Media;

/// <summary>
/// A copy of a clip made to be posted anywhere: under a size limit (30 MB unless set otherwise), at most 60 fps,
/// H.264 (which everything plays) and every audio track mixed into one. A higher frame rate can be blended into
/// motion blur rather than dropped (<see cref="MotionBlur"/>). The original is left alone.
/// </summary>
internal static class ShareCopy
{
    public const double LimitMB = 30;
    public const double MaxFps = 60;
    public const int AudioKbps = 128;

    // Below about this many bits per pixel, H.264 game footage turns to mush; a smaller picture looks better.
    private const double MinBitsPerPixel = 0.05;
    private static readonly int[] ShortSides = [1440, 1080, 720, 480, 360];

    /// <summary>GPU H.264 encoders first: sharing should be quick.</summary>
    public static string Encoder(IReadOnlySet<string> available) =>
        new[] { "h264_nvenc", "h264_amf", "h264_qsv" }.FirstOrDefault(available.Contains) ?? "libx264";

    /// <summary>How to make the copy. A clip that's already small enough H.264 keeps its video and only has its audio mixed.</summary>
    public static ExportRequest Plan(MediaInfo clip, string output, string encoderId, double limitMB = LimitMB, bool allowCopy = true,
        bool motionBlur = true)
    {
        if (clip.Duration <= 0) throw new ExportException("This clip has no length to share.");
        var audio = clip.EditableAudio.Count < clip.Audio.Count ? clip.Audio.Take(1) : clip.Audio;
        var request = new ExportRequest
        {
            Media = clip,
            OutputPath = output,
            Container = Containers.Mp4,
            Start = 0,
            End = clip.Duration,
            Tracks = audio.Select(a => new TrackExport(a.Index, a.Codec, a.Channels, a.BitRate, a.DisplayName, 1)).ToArray(),
            MergeAudio = true,
            AudioBitrateK = AudioKbps,
            EncoderId = encoderId,
            ForceAudioEncode = true,
        };
        return Plan(request, encoderId, limitMB, allowCopy, motionBlur);
    }

    internal static ExportRequest Plan(ExportRequest request, string encoderId, double limitMB, bool allowCopy, bool motionBlur)
    {
        var v = request.Media.Video ?? throw new ExportException("Sharing requires a video stream.");
        double fps = v.Fps > MaxFps + 0.01 ? MaxFps : 0;
        bool copy = allowCopy && v.Codec == "h264" && v.Fps > 0 && fps == 0 &&
            request.Crop == null && request.Start <= 0.0005 &&
            request.Media.Size * request.Duration / request.Media.Duration <= limitMB * TargetSizeExport.BytesPerMB;
        request = request with
        {
            Container = Containers.Mp4,
            MergeAudio = true,
            HybridAudio = false,
            EncoderId = encoderId,
            MaxShortSide = 0,
            AudioBitrateK = AudioKbps,
            ForceAudioEncode = true,
            VideoMode = copy ? VideoMode.Copy : VideoMode.Encode,
            Fps = fps,
            MotionBlur = motionBlur,
        };
        return TargetSizeExport.Plan(request, limitMB, adaptive: true);
    }

    /// <summary>
    /// The biggest picture (by its short side) that still gets enough bits per pixel at this bitrate: 0 to keep the
    /// clip's own size, otherwise the short side to scale it down to.
    /// </summary>
    public static int ShortSideFor(int width, int height, double fps, double videoBps)
    {
        int native = Math.Min(width, height);
        if (native <= 0 || fps <= 0) return 0;
        foreach (int side in ShortSides.Where(s => s < native).Prepend(native))
        {
            double scale = side / (double)native;
            if (videoBps / (width * scale * height * scale * fps) >= MinBitsPerPixel) return side == native ? 0 : side;
        }
        return native > ShortSides[^1] ? ShortSides[^1] : 0;
    }

    /// <summary>
    /// Makes the copy at <paramref name="output"/>, compressing again a little harder if the encoder overshot the limit.
    /// Progress is per attempt (the attempt number and 0..1). Returns the copy's size.
    /// </summary>
    public static async Task<long> CreateAsync(MediaInfo clip, string output, string encoderId, IProgress<(int Attempt, double Progress)> progress,
        CancellationToken ct, double limitMB = LimitMB, bool motionBlur = true)
    {
        var request = Plan(clip, output, encoderId, limitMB, motionBlur: motionBlur);
        return await TargetSizeExport.RunAsync(request, TargetSizeExport.Ceiling(request, limitMB), "libx264",
            progress, validating: null, ct);
    }
}
