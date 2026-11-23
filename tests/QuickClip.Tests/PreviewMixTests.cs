using System.Globalization;
using System.Text.RegularExpressions;
using QuickClip.Media;
using QuickClip.Mpv;
using Xunit.Abstractions;

namespace QuickClip.Tests;

/// <summary>
/// Plays the test files through libmpv with QuickClip's preview filter graph, records what would be heard
/// to a WAV file, and checks that each track is audible only at the volume it was given.
/// </summary>
[Collection("media")]
public sealed class PreviewMixTests(MediaFixture media, ITestOutputHelper output)
{
    private sealed class Session : IAsyncDisposable
    {
        public MpvPlayer Player { get; }
        public PreviewAudio Audio { get; }
        private TaskCompletionSource Loaded { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Ended { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Session(string? wav = null)
        {
            Assert.True(Deps.HasLibMpv, "libmpv-2.dll not found next to the tests.");
            Player = new MpvPlayer(IntPtr.Zero, new Dictionary<string, string>
            {
                ["vo"] = "null",
                ["force-window"] = "no",
                ["keep-open"] = "no",
                ["ao"] = wav == null ? "null" : "pcm",
                ["ao-pcm-file"] = wav ?? "",
                ["ao-pcm-waveheader"] = "yes",
            });
            Audio = new PreviewAudio(Player);
            Player.EventReceived += (id, _) =>
            {
                if (id == MpvEventId.FileLoaded) Loaded.TrySetResult();
                if (id == MpvEventId.EndFile && Loaded.Task.IsCompleted) Ended.TrySetResult();
            };
        }

        public async Task Load(string file)
        {
            Loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(Player.CommandAsync("loadfile", file));
            await Loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async Task Stop()
        {
            Assert.True(Player.Command("stop"));
            await Ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }

        public async Task PlayToEnd()
        {
            Player.SetProperty("pause", false);
            await Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        public async ValueTask DisposeAsync() => await Task.Run(Player.Dispose);
    }

    private Task<string> Record(string file, double[] volumes, string name) =>
        Record(file, name, s => s.Audio.Configure(volumes.Length, i => volumes[i]));

    private async Task<string> Record(string file, string name, Action<Session> configure, Action<Session>? onLoaded = null)
    {
        string wav = media.Out(name);
        await using (var s = new Session(wav))
        {
            configure(s);
            await s.Load(file);
            onLoaded?.Invoke(s);
            await s.PlayToEnd();
        }
        Assert.True(new FileInfo(wav).Length > 10_000, "no audio was recorded");
        return wav;
    }

    private async Task<string> MakeHybrid(string name)
    {
        string file = media.Out(name);
        await MediaFixture.Run(
        [
            "-f", "lavfi", "-i", $"aevalsrc=0.6*sin(2*PI*{MediaFixture.MicHz}*t)|0.6*sin(2*PI*{MediaFixture.MicHz}*t):s=48000",
            "-f", "lavfi", "-i", $"aevalsrc=0.6*sin(2*PI*{MediaFixture.DesktopHz}*t)|0.6*sin(2*PI*{MediaFixture.DesktopHz}*t):s=48000",
            "-i", media.TwoTracks, "-filter_complex", "[0:a]asplit[stem][mix];[mix]volume=0.25[full]",
            "-map", "[full]", "-map", "2:v", "-map", "[stem]", "-map", "1:a", "-t", "1.5",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-metadata:s:a:0", $"handler_name={ExportBuilder.FullMixTitle}",
            "-metadata:s:a:1", "handler_name=Mic", "-metadata:s:a:2", "handler_name=Desktop Audio",
            "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0", file,
        ]);
        return file;
    }

    private async Task<string> RemuxPlayback(string name, params string[] audioOptions)
    {
        string file = media.Out(name);
        await MediaFixture.Run(["-i", media.TwoTracks, "-map", "0:v:0", .. audioOptions, "-t", "1.5", "-c", "copy", file]);
        return file;
    }

    [Fact]
    public async Task Hybrid_playback_matches_the_full_mix_once()
    {
        string clip = await MakeHybrid("hybrid.mp4");
        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal([0, 2, 3], info.Audio.Select(track => track.Index));
        Assert.Equal([0, 1, 2], info.Audio.Select(track => track.Ordinal));
        Assert.Equal(["Mic", "Desktop Audio"], info.EditableAudio.Select(track => track.Title));

        string wav = await Record(clip, "hybrid-playback.wav", s => s.Audio.ConfigurePlayback(info), s =>
        {
            Assert.Equal("1", s.Player.GetString("aid"));
            Assert.Equal("audio", s.Player.GetString("current-tracks/audio/type"));
            Assert.Equal(0d, s.Player.GetDouble("current-tracks/audio/ff-index"));
        });
        double expectedMic = await MediaFixture.BandLevel(clip, 0, MediaFixture.MicHz, 0.25, 1);
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, 0.25, 1);
        double desktop = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, 0.25, 1);
        output.WriteLine($"Full Mix mic {expectedMic} dB, playback mic {mic} dB, playback desktop {desktop} dB");
        Assert.InRange(mic, expectedMic - 0.5, expectedMic + 0.5);
        Assert.True(mic - desktop > 30, $"playback mic {mic} dB, desktop {desktop} dB");
        var (code, _, log) = await Proc.RunAsync(Deps.FFmpeg!,
            ["-hide_banner", "-nostdin", "-i", wav, "-af", "volumedetect", "-f", "null", "-"]);
        Assert.Equal(0, code);
        var peakMatch = Regex.Match(log, @"max_volume:\s+(-?[\d.]+) dB");
        Assert.True(peakMatch.Success, log);
        double peak = double.Parse(peakMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        output.WriteLine($"Playback peak {peak} dBFS");
        Assert.InRange(peak, -20, -12);
    }

    [Theory]
    [InlineData("Mic", "Desktop Audio", true)]
    [InlineData(ExportBuilder.FullMixTitle, "Desktop Audio", false)]
    [InlineData("Mic", ExportBuilder.FullMixTitle, false)]
    [InlineData("QuickClip Full MiX", "Desktop Audio", true)]
    public async Task Stem_only_and_unrecognized_mix_metadata_keep_both_tracks(string first, string second, bool firstIsDefault)
    {
        string clip = await RemuxPlayback($"stems-{first}-{second}.mp4", "-map", "0:a",
            "-metadata:s:a:0", $"handler_name={first}", "-metadata:s:a:1", $"handler_name={second}",
            "-disposition:a:0", firstIsDefault ? "default" : "0", "-disposition:a:1", firstIsDefault ? "0" : "default");
        var info = await MediaInfo.ProbeAsync(clip);
        Assert.Equal([first, second], info.Audio.Select(track => track.Title));
        Assert.Equal(firstIsDefault, info.Audio[0].IsDefault);
        Assert.Null(info.PlaybackMix);

        string wav = await Record(clip, $"stems-{first}-{second}.wav", s => s.Audio.ConfigurePlayback(info));
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, 0.25, 1);
        double desktop = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, 0.25, 1);
        output.WriteLine($"Playback mic {mic} dB, desktop {desktop} dB");
        Assert.True(mic > -35 && desktop > -35, $"playback mic {mic} dB, desktop {desktop} dB");
    }

    [Fact]
    public async Task Selecting_another_hybrid_track_later_plays_only_that_track()
    {
        string clip = await MakeHybrid("switch-hybrid.mp4");
        var info = await MediaInfo.ProbeAsync(clip);
        string wav = await Record(clip, "switch-hybrid.wav", s =>
        {
            Assert.True(s.Player.SetProperty("volume", 80));
            Assert.True(s.Player.SetProperty("mute", true));
            s.Audio.ConfigurePlayback(info);
        }, s =>
        {
            Assert.Equal(80d, s.Player.GetDouble("volume"));
            Assert.Equal("yes", s.Player.GetString("mute"));
            Assert.True(s.Player.SetProperty("aid", "3"));
            Assert.Equal("3", s.Player.GetString("aid"));
            Assert.True(s.Player.SetProperty("mute", false));
        });
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, 0.25, 1);
        double desktop = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, 0.25, 1);
        Assert.True(desktop > -35 && desktop - mic > 30, $"playback mic {mic} dB, desktop {desktop} dB");
    }

    [Fact]
    public async Task One_track_named_full_mix_remains_audible()
    {
        string clip = await RemuxPlayback("single.mp4", "-map", "0:a:1", "-metadata:s:a:0",
            $"handler_name={ExportBuilder.FullMixTitle}", "-disposition:a:0", "default");
        var info = await MediaInfo.ProbeAsync(clip);
        Assert.True(Assert.Single(info.EditableAudio).IsPlaybackMix);
        Assert.Null(info.PlaybackMix);

        string wav = await Record(clip, "single.wav", s => s.Audio.ConfigurePlayback(info));
        Assert.True(await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, 0.25, 1) > -35);
    }

    [Fact]
    public async Task New_clips_and_probe_failure_clear_previous_audio_selection()
    {
        string hybrid = await MakeHybrid("next-hybrid.mp4");
        string silent = await RemuxPlayback("no-audio.mp4");
        string defaultSecond = await RemuxPlayback("second-default.mp4", "-map", "0:a",
            "-disposition:a:0", "0", "-disposition:a:1", "default");

        await using var s = new Session();
        s.Audio.ConfigurePlayback(await MediaInfo.ProbeAsync(media.TwoTracks));
        await s.Load(media.TwoTracks);
        Assert.Contains("[aid2]", s.Player.GetString("lavfi-complex"));
        await s.Stop();

        s.Audio.ConfigurePlayback(await MediaInfo.ProbeAsync(hybrid));
        await s.Load(hybrid);
        Assert.Equal("1", s.Player.GetString("aid"));
        Assert.Equal("", s.Player.GetString("lavfi-complex"));
        await s.Stop();

        s.Audio.ConfigurePlayback(await MediaInfo.ProbeAsync(silent));
        await s.Load(silent);
        Assert.Equal("no", s.Player.GetString("aid"));
        await s.Stop();

        // A null probe result is the same state Player passes after ffprobe throws.
        s.Audio.ConfigurePlayback(null);
        await s.Load(defaultSecond);
        Assert.Equal("2", s.Player.GetString("aid"));
        Assert.Equal("", s.Player.GetString("lavfi-complex"));
        await s.Stop();

        s.Audio.ConfigurePlayback(await MediaInfo.ProbeAsync(media.TwoTracks));
        await s.Load(media.TwoTracks);
        Assert.Contains("[aid2]", s.Player.GetString("lavfi-complex"));
        await s.Stop();

        string wav = await Record(defaultSecond, "fallback.wav", fallback => fallback.Audio.ConfigurePlayback(null));
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, 0.25, 1);
        double desktop = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, 0.25, 1);
        output.WriteLine($"Fallback mic {mic} dB, desktop {desktop} dB");
        Assert.True(desktop > -35 && desktop - mic > 30, $"playback mic {mic} dB, desktop {desktop} dB");
    }

    [Fact]
    public void Hidden_hybrid_composite_is_not_a_preview_input()
    {
        var (graph, volumeGraph) = PreviewAudio.Build([2, 3, 4], i => i == 1 ? 0.5 : 1);
        Assert.Contains("[aid2]", graph);
        Assert.Contains("[aid3]", graph);
        Assert.Contains("[aid4]", graph);
        Assert.DoesNotContain("[aid1]", graph);
        Assert.Contains("volume@t2=0.5", volumeGraph);
    }

    [Fact]
    public async Task Each_of_two_tracks_plays_at_its_own_volume()
    {
        string micOnly = await Record(media.TwoTracks, [1, 0], "mic-only.wav");
        string deskOnly = await Record(media.TwoTracks, [0, 1], "desk-only.wav");

        double micInMic = await MediaFixture.BandLevel(micOnly, 0, MediaFixture.MicHz);
        double deskInMic = await MediaFixture.BandLevel(micOnly, 0, MediaFixture.DesktopHz);
        double micInDesk = await MediaFixture.BandLevel(deskOnly, 0, MediaFixture.MicHz);
        double deskInDesk = await MediaFixture.BandLevel(deskOnly, 0, MediaFixture.DesktopHz);

        Assert.True(micInMic - deskInMic > 30, $"mic-only: mic {micInMic} dB, desktop {deskInMic} dB");
        Assert.True(deskInDesk - micInDesk > 30, $"desktop-only: mic {micInDesk} dB, desktop {deskInDesk} dB");
    }

    [Fact]
    public async Task Three_tracks_with_different_sample_rates_can_be_isolated()
    {
        string wav = await Record(media.ThreeTracks, [0, 1, 0], "middle-only.wav");
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz);
        double game = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz);
        double discord = await MediaFixture.BandLevel(wav, 0, MediaFixture.ThirdHz);
        Assert.True(game - mic > 30 && game - discord > 30, $"mic {mic}, game {game}, discord {discord} dB");
    }

    [Fact]
    public async Task Live_volume_change_applies_while_playing()
    {
        string wav = media.Out("live-only.wav");
        double[] volumes = [1, 1];
        await using (var s = new Session(wav))
        {
            s.Audio.Configure(2, i => volumes[i]);
            await s.Load(media.TwoTracks);
            s.Player.SetProperty("pause", false);
            for (int i = 0; i < 200 && (s.Player.GetDouble("time-pos") ?? 0) < 1; i++) await Task.Delay(20);
            volumes[0] = 0;
            s.Audio.SetLive(2, i => volumes[i]);
            await s.Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, fromSeconds: 2.5);
        double desk = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, fromSeconds: 2.5);
        Assert.True(desk - mic > 30, $"after live change: mic {mic} dB, desktop {desk} dB");
    }

    [Fact]
    public async Task Live_volume_change_survives_a_seek()
    {
        string wav = media.Out("live-change.wav");
        double[] volumes = [1, 1];
        await using (var s = new Session(wav))
        {
            s.Audio.Configure(2, i => volumes[i]);
            await s.Load(media.TwoTracks);
            s.Player.SetProperty("pause", false);
            for (int i = 0; i < 200 && (s.Player.GetDouble("time-pos") ?? 0) < 1; i++) await Task.Delay(20);

            // What the UI does when the desktop slider goes to 0: live change, then sync, then the user seeks.
            volumes[1] = 0;
            s.Audio.SetLive(2, i => volumes[i]);
            s.Audio.Sync(2, i => volumes[i]);
            s.Player.Command("seek", "0", "absolute+exact");
            await s.Ended.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }

        // Everything recorded after the first ~1.5 s comes from the replay after the seek.
        double mic = await MediaFixture.BandLevel(wav, 0, MediaFixture.MicHz, fromSeconds: 2.5);
        double desk = await MediaFixture.BandLevel(wav, 0, MediaFixture.DesktopHz, fromSeconds: 2.5);
        Assert.True(mic - desk > 30, $"after seek: mic {mic} dB, desktop {desk} dB");
    }
}
