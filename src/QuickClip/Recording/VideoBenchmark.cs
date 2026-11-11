using System.Diagnostics;
using System.Text.Json;

namespace QuickClip.Recording;

/// <summary>Bounded, explicit, video-only diagnostic run. No hotkey, gallery, playback, audio or media export.</summary>
internal static class VideoBenchmark
{
    internal sealed record Options(bool AllowDesktopCapture = false, int Seconds = 10, string Monitor = "", int Fps = 60,
        int Height = 0, string Encoder = "hevc_nvenc", string Preset = "p4", string Backend = "DesktopDuplication", int? VideoKbps = 20000);
    private sealed record Sample(DateTimeOffset Utc, double ElapsedSeconds, double CpuPercentMachine, long WorkingSetBytes, long PrivateBytes,
        double SubmittedFps, long ReplayBytes, double ReplaySeconds, long MissingTimelineSlots, string Diagnostics);

    internal static void Validate(Options options)
    {
        if (!options.AllowDesktopCapture || options.Seconds is < 5 or > 30 || options.Fps is < 15 or > 240 ||
            options.Height < 0 || options.Height > 4320 ||
            options.Encoder is not ("h264_nvenc" or "hevc_nvenc") || options.Preset is not ("p4" or "p3" or "p2") ||
            !Enum.TryParse<CaptureEngine.MonitorBackend>(options.Backend, out var backend) || !Enum.IsDefined(backend))
            throw new ArgumentException("Benchmark needs explicit desktop consent, 5–30 seconds, 15–240 fps, NVENC and a supported preset/backend.");
        RecordingPresets.ValidateTargetVideoKbps(options.VideoKbps);
    }

    internal static async Task<int> RunAsync(string input, string output)
    {
        bool started = false;
        try
        {
            var options = JsonSerializer.Deserialize<Options>(File.ReadAllText(input)) ?? new();
            Validate(options);
            CaptureEngine.Initialize();
            var monitor = Monitors.List().FirstOrDefault(m => options.Monitor == "" ? m.IsPrimary : m.DeviceName == options.Monitor)
                ?? throw new InvalidOperationException("Selected monitor unavailable.");
            var size = RecordingPresets.OutputSize(monitor.Width, monitor.Height, options.Height);
            int targetVideoKbps = RecordingPresets.VideoKbps(RecordingQuality.High, size.Width, size.Height, options.Fps,
                options.Encoder == "hevc_nvenc" ? "hevc" : "h264", options.VideoKbps);
            var video = new CaptureEngine.VideoOptions
            {
                Backend = Enum.Parse<CaptureEngine.MonitorBackend>(options.Backend),
                Preset = options.Preset == "p2" ? CaptureEngine.NvencSpeed.FastestP2 : options.Preset == "p3" ? CaptureEngine.NvencSpeed.FasterP3 : CaptureEngine.NvencSpeed.BalancedP4,
                Diagnostics = 1,
            };
            string? error = CaptureEngine.Start(monitor.DeviceName, "", options.Fps, size.Width, size.Height, options.Encoder,
                targetVideoKbps, 192, 5, false, false, false, [], false, [], video);
            if (error != null) throw new InvalidOperationException(error);
            started = true;
            var samples = new List<Sample>();
            using var process = Process.GetCurrentProcess();
            var timer = Stopwatch.StartNew();
            TimeSpan cpu = process.TotalProcessorTime;
            double previous = 0;
            while (timer.Elapsed.TotalSeconds < options.Seconds)
            {
                await Task.Delay(1000);
                process.Refresh();
                double elapsed = timer.Elapsed.TotalSeconds;
                TimeSpan nextCpu = process.TotalProcessorTime;
                var stats = CaptureEngine.GetStats();
                samples.Add(new(DateTimeOffset.UtcNow, elapsed, (nextCpu - cpu).TotalSeconds / (elapsed - previous) / Environment.ProcessorCount * 100,
                    process.WorkingSet64, process.PrivateMemorySize64, stats.Fps, stats.BufferBytes, stats.BufferSeconds,
                    stats.DroppedFrames, CaptureEngine.VideoDiagnostics()));
                cpu = nextCpu; previous = elapsed;
            }
            File.WriteAllText(output, JsonSerializer.Serialize(new { Options = options, TargetVideoKbps = targetVideoKbps, Displays = Monitors.Diagnostics(), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, JsonSerializer.Serialize(new { Error = ex.Message }));
            return 1;
        }
        finally { if (started) CaptureEngine.Stop(); }
    }
}
