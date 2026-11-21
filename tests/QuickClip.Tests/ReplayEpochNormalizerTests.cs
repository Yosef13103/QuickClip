using System.Diagnostics;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochNormalizerTests
{
    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-000000000201");

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public async Task Mixed_resolution_segments_produce_one_decodable_mp4_with_two_audio_stems(string inputExtension)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        Assert.True(File.Exists(ffmpeg), "Bundled FFmpeg unavailable.");
        Assert.True(File.Exists(ffprobe), "Bundled FFprobe unavailable.");

        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var first = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 640, 360, "h264+aac-stems");
            var second = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                2_000_000, 4_000_000, 960, 400, "h264+aac-stems");

            string a = Path.Combine(temp, "first" + inputExtension), b = Path.Combine(temp, "second" + inputExtension);
            await MakeFixture(ffmpeg, a, first.Width, first.Height, 440);
            await MakeFixture(ffmpeg, b, second.Width, second.Height, 550);

            var plan = ReplayEpochPlanner.Select([first, second], Session,
                "verified-game:synthetic", 0, 4_000_000, 8_000_000);
            Assert.True(plan.RequiresVideoNormalization);
            string resultPath = Path.Combine(temp, "joined.mp4");
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(first.EpochId, a), new(second.EpochId, b)],
                resultPath, ffmpeg, ffprobe);
            Assert.Equal((960, 400, 2), (result.Width, result.Height, result.AudioTracks));
            Assert.InRange(result.Duration.TotalSeconds, 3.9, 4.15);
            Assert.True(File.Exists(resultPath));
            Assert.True(File.Exists(a) && File.Exists(b)); // Original encoded segments untouched.
            Assert.Equal(3, Directory.GetFiles(temp).Length);
            Assert.DoesNotContain(Directory.EnumerateFiles(temp), file => file.Contains(".partial.", StringComparison.OrdinalIgnoreCase));

            // Actually decode both video and audio tracks; metadata-only validation isn't enough.
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", resultPath,
                "-map", "0", "-f", "null", "-"]);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task Opted_in_gap_policy_skips_unavailable_footage_and_reports_omitted_duration()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var first = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 640, 360, "h264+aac");
            var second = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                4_000_000, 6_000_000, 960, 400, "h264+aac");
            string a = Path.Combine(temp, "before.mkv"), b = Path.Combine(temp, "after.mkv");
            await MakeFixture(ffmpeg, a, 640, 360, 440);
            await MakeFixture(ffmpeg, b, 960, 400, 550);
            var plan = ReplayEpochPlanner.Select([first, second], Session,
                "verified-game:synthetic", 0, 6_000_000, 8_000_000);
            Assert.Equal((2_000_000L, 4_000_000L),
                (Assert.Single(plan.Gaps).StartUs, plan.Gaps[0].EndUs));
            string output = Path.Combine(temp, "joined-with-gap-omitted.mp4");
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(first.EpochId, a), new(second.EpochId, b)],
                output, ffmpeg, ffprobe, gapPolicy: ReplayEpochGapPolicy.OmitMissingFootage);
            Assert.Equal(TimeSpan.FromSeconds(2), result.OmittedMissingFootage);
            Assert.InRange(result.Duration.TotalSeconds, 3.9, 4.15);
            Assert.Equal((960, 400, 2), (result.Width, result.Height, result.AudioTracks));
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", output, "-map", "0", "-f", "null", "-"]);
            Assert.True(File.Exists(a) && File.Exists(b));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Different_named_audio_stems_are_rejected_instead_of_silently_swapped()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var one = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 320, 180, "same-video");
            var two = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                2_000_000, 4_000_000, 320, 180, "same-video");
            string a = Path.Combine(temp, "first.mkv"), b = Path.Combine(temp, "second.mkv");
            await MakeFixture(ffmpeg, a, 320, 180, 440, "Game");
            await MakeFixture(ffmpeg, b, 320, 180, 550, "Voice");
            var plan = ReplayEpochPlanner.Select([one, two], Session, "verified-game:synthetic",
                0, 4_000_000, 8_000_000);
            string output = Path.Combine(temp, "should-not-exist.mp4");
            await Assert.ThrowsAsync<InvalidOperationException>(() => ReplayEpochNormalizer.ExportAsync(
                plan, [new(one.EpochId, a), new(two.EpochId, b)], output, ffmpeg, ffprobe));
            Assert.False(File.Exists(output));
            Assert.True(File.Exists(a) && File.Exists(b));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Exporter_rejects_gaps_without_fabricating_video_or_touching_files()
    {
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var first = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 1_000_000, 640, 360, "h264");
            var second = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                2_000_000, 3_000_000, 640, 360, "h264");
            var plan = ReplayEpochPlanner.Select([first, second], Session,
                "verified-game:synthetic", 0, 3_000_000, 8_000_000);
            string output = Path.Combine(temp, "rejected.mp4");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ReplayEpochNormalizer.ExportAsync(plan, [],
                    output, "unused ffmpeg path", "unused ffprobe path"));
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task MakeFixture(string ffmpeg, string file, int width, int height, int hz, string firstAudioTitle = "Game") =>
        Run(ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration=2",
            "-f", "lavfi", "-i", $"sine=frequency={hz}:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", $"sine=frequency={hz+100}:sample_rate=48000:duration=2",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k", "-shortest",
            "-metadata:s:a:0", "title=" + firstAudioTitle, "-metadata:s:a:1", "title=Microphone", file]);

    private static async Task Run(string exe, IReadOnlyList<string> args)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var info = new ProcessStartInfo(exe) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token);
            string failure = await stderr;
            _ = await stdout;
            Assert.True(process.ExitCode == 0, failure);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
