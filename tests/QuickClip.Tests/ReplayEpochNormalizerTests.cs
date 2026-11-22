using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class ReplayEpochNormalizerTests
{
    [DllImport("QuickClipCapture.dll", CharSet = CharSet.Unicode, CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_test_detached_epoch_media_roundtrip(
        string sourceA, string sourceB, string outputA, string outputB,
        [Out] char[] error, int errorLength);

    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-000000000201");

    [Theory]
    [InlineData(".mp4", ".mp4")]
    [InlineData(".mkv", ".mp4")]
    [InlineData(".mkv", ".mkv")]
    public async Task Mixed_resolution_segments_produce_one_decodable_export_with_two_audio_stems(
        string inputExtension, string outputExtension)
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
            string resultPath = Path.Combine(temp, "joined" + outputExtension);
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(first.EpochId, a), new(second.EpochId, b)],
                resultPath, ffmpeg, ffprobe);
            Assert.Equal((960, 400, 2), (result.Width, result.Height, result.AudioTracks));
            Assert.False(result.StreamCopied); // Mixed geometry must normalize, not concatenate incompatible codecs.
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

    [Theory]
    [InlineData(".mp4", 30, true)]
    [InlineData(".mkv", 30, true)]
    [InlineData(".mkv", 60, false)]
    public async Task Single_epoch_stream_copy_only_when_geometry_and_fps_are_compatible(
        string extension, int outputFps, bool expectedCopy)
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var single = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 640, 360, "h264+aac");
            string input = Path.Combine(temp, "source.mkv");
            string target = Path.Combine(temp, "saved" + extension);
            await MakeFixture(ffmpeg, input, 640, 360, 440);
            var plan = ReplayEpochPlanner.Select([single], Session,
                "verified-game:synthetic", 0, 2_000_000, 34_000_000);
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(single.EpochId, input)], target, ffmpeg, ffprobe, fps: outputFps);
            Assert.Equal(expectedCopy, result.StreamCopied);
            Assert.Equal((640, 360, 2), (result.Width, result.Height, result.AudioTracks));
            Assert.InRange(result.Duration.TotalSeconds, 1.9, 2.15);
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", target,
                "-map", "0", "-f", "null", "-"]);
            Assert.True(File.Exists(input));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Custom_canvas_normalizes_mixed_sources_to_exact_size_without_touching_mkv_inputs()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Canvas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var one = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 640, 360, "h264+aac");
            var two = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                2_000_000, 4_000_000, 960, 400, "h264+aac");
            string a = Path.Combine(temp, "one.mkv"), b = Path.Combine(temp, "two.mkv");
            await MakeFixture(ffmpeg, a, 640, 360, 440);
            await MakeFixture(ffmpeg, b, 960, 400, 550);
            string target = Path.Combine(temp, "custom.mkv");
            var plan = ReplayEpochPlanner.Select([one, two], Session,
                "verified-game:synthetic", 0, 4_000_000, 34_000_000);
            var output = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(one.EpochId, a), new(two.EpochId, b)], target, ffmpeg, ffprobe,
                outputOptions: new(ReplayEpochOutputSize.Custom, 480, 200));
            Assert.Equal((480, 200, 2), (output.Width, output.Height, output.AudioTracks));
            Assert.True(File.Exists(a) && File.Exists(b) && File.Exists(target));
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", target, "-map", "0", "-f", "null", "-"]);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Mkv_to_mp4_remux_uses_stream_copy_and_keeps_two_audio_stems()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Remux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string input = Path.Combine(temp, "original.mkv");
            string target = Path.Combine(temp, "lossless-container-conversion.mp4");
            await MakeFixture(ffmpeg, input, 960, 400, 440);
            byte[] original = await File.ReadAllBytesAsync(input);
            var result = await ReplayEpochNormalizer.RemuxMkvToMp4Async(
                input, target, ffmpeg, ffprobe);
            Assert.Equal((960, 400, 2), (result.Width, result.Height, result.AudioTracks));
            Assert.InRange(result.Duration.TotalSeconds, 1.9, 2.1);
            Assert.Equal(original, await File.ReadAllBytesAsync(input));
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", target,
                "-map", "0", "-f", "null", "-"]);
            Assert.Empty(Directory.EnumerateFiles(temp, "*.partial.mp4"));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Native_detached_encoded_packets_roundtrip_into_decodable_mkv_and_joined_mp4()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        Assert.True(CaptureEngine.IsAvailable);
        CaptureEngine.Initialize();
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-DetachedMedia-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string inputA = Path.Combine(temp, "inputA.mp4"), inputB = Path.Combine(temp, "inputB.mp4");
            string detachedA = Path.Combine(temp, "detachedA.mkv"), detachedB = Path.Combine(temp, "detachedB.mkv");
            string output = Path.Combine(temp, "joined.mp4");
            await MakeFixture(ffmpeg, inputA, 640, 360, 440);
            await MakeFixture(ffmpeg, inputB, 960, 400, 550);

            var error = new char[2048];
            int ok = qc_test_detached_epoch_media_roundtrip(
                inputA, inputB, detachedA, detachedB, error, error.Length);
            Assert.True(ok == 1, "Native detached media failure: " + new string(error).TrimEnd('\0'));
            Assert.True(File.Exists(detachedA) && File.Exists(detachedB));
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", detachedA, "-map", "0", "-f", "null", "-"]);
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", detachedB, "-map", "0", "-f", "null", "-"]);

            var first = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 640, 360, "h264-aac");
            var second = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                3_000_000, 5_000_000, 960, 400, "h264-aac");
            var plan = ReplayEpochPlanner.Select([first, second], Session,
                "verified-game:synthetic", 0, 5_000_000, 8_000_000);
            Assert.Single(plan.Gaps);
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(first.EpochId, detachedA), new(second.EpochId, detachedB)],
                output, ffmpeg, ffprobe, gapPolicy: ReplayEpochGapPolicy.OmitMissingFootage);
            Assert.Equal((960, 400, 2), (result.Width, result.Height, result.AudioTracks));
            Assert.Equal(TimeSpan.FromSeconds(1), result.OmittedMissingFootage);
            Assert.InRange(result.Duration.TotalSeconds, 3.9, 4.15);
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", output, "-map", "0", "-f", "null", "-"]);
            Assert.True(File.Exists(inputA) && File.Exists(inputB));
            Assert.Empty(Directory.EnumerateFiles(temp, "*.partial.mp4"));
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


    [Fact]
    public async Task Mixed_av1_epochs_keep_av1_and_original_named_audio_tracks()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-AV1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var first = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                0, 2_000_000, 320, 180, "av1+aac");
            var second = new ReplayEncodedEpoch(Session, "verified-game:synthetic", Guid.NewGuid(),
                2_000_000, 4_000_000, 480, 200, "av1+aac");
            string a = Path.Combine(temp, "av1-a.mkv"), b = Path.Combine(temp, "av1-b.mkv");
            await MakeAv1Fixture(ffmpeg, a, first.Width, first.Height);
            await MakeAv1Fixture(ffmpeg, b, second.Width, second.Height);
            var plan = ReplayEpochPlanner.Select([first, second], Session,
                "verified-game:synthetic", 0, 4_000_000, 8_000_000);
            string target = Path.Combine(temp, "joined-av1.mp4");
            var result = await ReplayEpochNormalizer.ExportAsync(plan,
                [new(first.EpochId, a), new(second.EpochId, b)], target, ffmpeg, ffprobe);
            Assert.False(result.StreamCopied);
            Assert.Equal("av1", result.VideoCodec);
            Assert.Equal(2, result.AudioTracks);
            await Run(ffmpeg, ["-hide_banner", "-v", "error", "-nostdin", "-i", target,
                "-map", "0", "-f", "null", "-"]);
            Assert.True(File.Exists(a) && File.Exists(b));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [Fact]
    public async Task Invalid_epoch_encoder_does_not_create_an_output()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-Codec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var epoch = new ReplayEncodedEpoch(Session, "verified-game:synthetic",
                Guid.NewGuid(), 0, 2_000_000, 320, 180, "h264");
            string src = Path.Combine(temp, "source.mkv"), output = Path.Combine(temp, "invalid.mp4");
            await MakeFixture(ffmpeg, src, 320, 180, 440);
            var plan = ReplayEpochPlanner.Select([epoch], Session,
                "verified-game:synthetic", 0, 2_000_000, 8_000_000);
            await Assert.ThrowsAsync<ArgumentException>(() => ReplayEpochNormalizer.ExportAsync(
                plan, [new(epoch.EpochId, src)], output, ffmpeg, ffprobe, fps: 60,
                encodingOptions: new("some_fake_unsafe_encoder")));
            Assert.False(File.Exists(output));
            Assert.True(File.Exists(src));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }


    [Fact]
    public async Task Explicit_save_joins_detached_mkv_and_current_mp4_discloses_gap_and_cleans_scratch()
    {
        string ffmpeg = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        string ffprobe = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        string temp = Path.Combine(Path.GetTempPath(), "QuickClip-Epoch-LiveSave-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string archive = Path.Combine(temp, "archive.mkv");
            string active = Path.Combine(temp, "active.mp4");
            await MakeFixture(ffmpeg, archive, 640, 360, 440);
            await MakeFixture(ffmpeg, active, 960, 400, 550);
            var proof = new ReplayEpochGameProof("synthetic:game", @"C:\Games\synthetic.exe",
                new WindowOwner(new IntPtr(1), 1, 1, 1), "DISPLAY-A", 1, 1, 1,
                true, true, true);
            Guid session = Guid.NewGuid();
            var retained = new[] { new CaptureEngine.EpochDescriptor
            {
                EpochId = 7, FirstUs = 0, LastUs = 2_000_000,
                Width = 640, Height = 360, CompressedBytes = new FileInfo(archive).Length
            } };
            var currentSpan = new CaptureEngine.ReplaySpan
            {
                FirstUs = 0, LastUs = 1_966_667, DurationUs = 2_000_000,
            };
            var currentVideo = new CaptureEngine.ReplayVideo
            {
                Width = 960, Height = 400, FpsNumerator = 30, FpsDenominator = 1,
            };
            var request = new CaptureEngine.ReplayPosition { EventUs = 2_000_000 };
            var result = await ReplayEpochLiveSaver.MergeAsync(
                session, proof, retained, 3_000_000, request, 5, currentSpan, currentVideo,
                30, active, temp, ffmpeg, ffprobe, CancellationToken.None,
                syntheticEpochWriter: (id, destination, _) =>
                {
                    Assert.Equal(7UL, id);
                    File.Copy(archive, destination);
                    return Task.CompletedTask;
                });
            Assert.NotNull(result);
            Assert.Equal(2, result.EpochCount);
            Assert.Equal(TimeSpan.FromSeconds(1), result.Omitted);
            Assert.InRange(result.Duration.TotalSeconds, 3.9, 4.15);
            Assert.Equal((960, 400), (result.Width, result.Height));
            Assert.True(File.Exists(archive) && File.Exists(active) && File.Exists(result.Path));
            Assert.DoesNotContain(Directory.GetDirectories(temp), name =>
                Path.GetFileName(name).StartsWith(".qc-e-", StringComparison.OrdinalIgnoreCase));
            await Run(ffmpeg, ["-v", "error", "-nostdin", "-i", result.Path,
                "-map", "0", "-f", "null", "-"]);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task MakeAv1Fixture(string ffmpeg, string file, int width, int height) =>
        Run(ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=550:sample_rate=48000:duration=2",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
            "-c:v", "libsvtav1", "-preset", "12", "-threads", "2", "-crf", "35",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "64k", "-shortest",
            "-metadata:s:a:0", "title=Game", "-metadata:s:a:1", "title=Microphone", file]);

    private static Task MakeFixture(string ffmpeg, string file, int width, int height, int hz, string firstAudioTitle = "Game") =>
        Run(ffmpeg,
            ["-hide_banner", "-v", "error", "-nostdin", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration=2",
            "-f", "lavfi", "-i", $"sine=frequency={hz}:sample_rate=48000:duration=2",
            "-f", "lavfi", "-i", $"sine=frequency={hz+100}:sample_rate=48000:duration=2",
            "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "96k", "-shortest",
            "-metadata:s:a:0", "title=" + firstAudioTitle, "-metadata:s:a:0", "handler_name=" + firstAudioTitle,
            "-metadata:s:a:1", "title=Microphone", "-metadata:s:a:1", "handler_name=Microphone", file]);

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
