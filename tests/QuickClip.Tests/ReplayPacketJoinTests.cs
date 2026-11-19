using System.Runtime.InteropServices;
using System.Security.Cryptography;
using QuickClip.Media;
using QuickClip.Library;
using QuickClip.Recording;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class ReplayPacketJoinTests(MediaFixture media, Xunit.Abstractions.ITestOutputHelper log)
{
    [Fact]
    public async Task Native_packet_union_matches_continuous_video_and_all_decoded_audio_samples()
    {
        string master = media.Out("continuous-encode.mp4");
        await MediaFixture.Run([
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30:duration=9",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=9",
            "-f", "lavfi", "-i", "sine=frequency=733:sample_rate=48000:duration=9",
            "-f", "lavfi", "-i", "sine=frequency=1171:sample_rate=48000:duration=9",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a",
            "-c:v", "libx264", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "0",
            "-threads", "2", "-c:a", "aac", "-t", "9",
            "-metadata:s:a:0", "handler_name=QuickClip Full Mix",
            "-metadata:s:a:1", "handler_name=Game", "-metadata:s:a:2", "handler_name=Mic", master,
        ]);
        var reference = Snapshot(master, "continuous-native", 0, 8_999_999);
        var a = Snapshot(master, "overlap-a", 0, 5_999_999);
        var b = Snapshot(master, "overlap-b", 3_000_000, 8_999_999);
        string joined = media.Out("native-union.mp4"), joinedProof = media.Out("native-union.proof");
        ReplayNative.Join([a.Media, b.Media], [a.Proof, b.Proof], joined, joinedProof, TimeSpan.FromMinutes(1), null, default);
        await Compare(reference.Media, joined);

        // Duplicate and contained saves must not introduce even one extra AAC packet.
        var contained = Snapshot(master, "contained", 1_000_000, 2_999_999);
        string duplicated = media.Out("duplicate-union.mp4");
        ReplayNative.Join([a.Media, a.Media, contained.Media, b.Media], [a.Proof, a.Proof, contained.Proof, b.Proof],
            duplicated, media.Out("duplicate-union.proof"), TimeSpan.FromMinutes(1), null, default);
        await Compare(reference.Media, duplicated);
        // Independently exercise the native-to-durable-store boundary, including
        // prior joined results, stale snapshots, and missing provenance.
        string db = media.Out("replay-provenance.sqlite3");
        var store = new ReplayProvenanceStore(db);
        var session = Guid.NewGuid();
        var original = store.FinalizeAndAdd(MediaSource.Capture(a.Media),
            MediaSource.Capture(media.Out("overlap-a.pending")), session, 0, 6_000_000, null, default);
        var later = store.FinalizeAndAdd(MediaSource.Capture(b.Media),
            MediaSource.Capture(media.Out("overlap-b.pending")), session, 3_000_000, 9_000_000, null, default);
        var keys = new HashSet<string>([LibraryFiles.PathKey(a.Media), LibraryFiles.PathKey(b.Media)]);
        store = new ReplayProvenanceStore(db);
        var cluster = Assert.Single(ReplayPlanner.Find(store.ReadCandidates(keys)));
        Assert.Equal(9_000_000, cluster.UnionUs);
        Assert.Equal(3_000_000, cluster.DuplicateUs);
        Assert.Equal(File.ReadAllBytes(a.Proof), store.ReadProof(original));
        Assert.Empty(store.ReadCandidates(new HashSet<string>()));
        Assert.Throws<ExportException>(() => store.Add(MediaSource.Capture(a.Media), session,
            0, 6_000_000, File.ReadAllBytes(media.Out("overlap-a.pending"))));

        var combined = store.Add(MediaSource.Capture(joined), session, 0, 9_000_000, File.ReadAllBytes(joinedProof));
        Assert.Equal(original.Compatibility, combined.Compatibility);
        string chained = media.Out("chained-union.mp4");
        ReplayNative.Join([joined, b.Media], [joinedProof, b.Proof], chained,
            media.Out("chained-union.proof"), TimeSpan.FromMinutes(1), null, default);
        await Compare(reference.Media, chained);

        var otherEpoch = Snapshot(master, "other-epoch", 3_000_000, 8_999_999, 18);
        string rejected = media.Out("rejected-union.mp4");
        Assert.Throws<ExportException>(() => ReplayNative.Join([a.Media, otherEpoch.Media], [a.Proof, otherEpoch.Proof],
            rejected, media.Out("rejected.proof"), TimeSpan.FromMinutes(1), null, default));
        Assert.False(File.Exists(rejected));
        Assert.Throws<ExportException>(() => ReplayNative.Join([a.Media, b.Media], [a.Proof, b.Proof],
            rejected, media.Out("rejected.proof"), TimeSpan.FromSeconds(8), null, default));
        Assert.False(File.Exists(rejected));

        File.AppendAllText(b.Media, "external modification");
        Assert.Throws<ExportException>(() => store.ReadProof(later));
        Assert.Equal(original.Id, Assert.Single(store.ReadCandidates(keys)).Id);
        store.Invalidate(a.Media);
        Assert.Throws<ExportException>(() => store.ReadProof(original));
        Assert.Empty(store.ReadCandidates(keys));
    }

    private (string Media, string Proof) Snapshot(string reference, string name, long from, long to, long epoch = 17)
    {
        CaptureEngine.Initialize();
        string path = media.Out(name + ".mp4"), pending = media.Out(name + ".pending"), proof = media.Out(name + ".proof");
        var error = new char[1024];
        int result = qc_test_replay_snapshot(reference, path, pending, from, to, epoch, error, error.Length);
        Assert.True(result != 0, new string(error).TrimEnd('\0'));
        ReplayNative.FinalizeProof(path, pending, proof, null, default);
        return (path, proof);
    }

    private async Task Compare(string expected, string actual)
    {
        var a = await MediaInfo.ProbeAsync(expected);
        var b = await MediaInfo.ProbeAsync(actual);
        Assert.Equal(a.Duration, b.Duration, precision: 5);
        Assert.Equal(3, b.Audio.Count);
        Assert.Equal(a.Audio.Select(t => t.Title), b.Audio.Select(t => t.Title));
        var framesA = await Frames(expected);
        var framesB = await Frames(actual);
        Assert.Equal(270, framesA.Length);
        Assert.Equal(framesA, framesB);
        for (int track = 0; track < 3; ++track)
        {
            string first = media.Out(Guid.NewGuid() + ".pcm"), second = media.Out(Guid.NewGuid() + ".pcm");
            await MediaFixture.Run(["-i", expected, "-map", $"0:a:{track}", "-c:a", "pcm_s32le", "-f", "s32le", first]);
            await MediaFixture.Run(["-i", actual, "-map", $"0:a:{track}", "-c:a", "pcm_s32le", "-f", "s32le", second]);
            Assert.Equal(new FileInfo(first).Length, new FileInfo(second).Length);
            using var firstStream = File.OpenRead(first);
            using var secondStream = File.OpenRead(second);
            Assert.Equal(SHA256.HashData(firstStream), SHA256.HashData(secondStream));
            log.WriteLine($"Track {track}: {new FileInfo(first).Length} decoded PCM bytes identical.");
        }
    }

    private static async Task<string[]> Frames(string path)
    {
        var (code, text, error) = await Proc.RunAsync(Deps.FFmpeg!,
            ["-v", "error", "-i", path, "-map", "0:v", "-f", "framemd5", "-"]);
        Assert.True(code == 0, error);
        return text.Split('\n').Where(line => line.Length > 0 && !line.StartsWith('#')).ToArray();
    }

    [DllImport("QuickClipCapture.dll", CharSet = CharSet.Unicode)]
    private static extern int qc_test_replay_snapshot(string reference, string destination, string pending,
        long fromUs, long toUs, long epoch, [Out] char[] error, int length);
}
