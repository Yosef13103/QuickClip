using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class RecordingVideoOptionsTests
{
    private static AppSettings Defaults() => new()
    {
        RecordAspect = "21:9", RecordResolution = 1440, RecordFps = 60,
        RecordQuality = RecordingQuality.High, RecordEncoder = "h264_nvenc",
        NvencPreset = CaptureEngine.NvencSpeed.FastestP2, BufferSeconds = 60,
    };

    [Fact]
    public void Sparse_options_preserve_globals_and_are_immutable_after_activation()
    {
        var defaults = Defaults();
        var policy = new GameRecordingPolicy
        {
            Aspect = "16:9", ResolutionShortSide = 1000, Fps = 30,
            Quality = RecordingQuality.Medium, EncoderId = "hevc_nvenc",
            NvencPreset = CaptureEngine.NvencSpeed.FasterP3, ReplaySeconds = 120,
            Bitrate = new(GameRateControl.CustomBitrate, 12500),
        };
        var active = RecordingVideoOptions.Resolve(defaults, policy);
        Assert.Equal(new RecordingVideoOptions("16:9", 1000, 30, RecordingQuality.Medium,
            "hevc_nvenc", null, CaptureEngine.NvencSpeed.FasterP3, 120, 12500), active);
        Assert.Equal(60, defaults.BufferSeconds);
        Assert.Equal("21:9", defaults.RecordAspect);
        Assert.Equal("h264_nvenc", defaults.RecordEncoder);
        defaults.RecordFps = 90;
        defaults.BufferSeconds = 30;
        Assert.Equal(30, active.Fps);
        Assert.Equal(120, active.ReplaySeconds);
        var inherited = RecordingVideoOptions.Resolve(defaults, policy with { Fps = null, ReplaySeconds = null });
        Assert.Equal(90, inherited.Fps);
        Assert.Equal(30, inherited.ReplaySeconds);
        var manual = RecordingVideoOptions.Resolve(defaults);
        Assert.Equal("21:9", manual.Aspect);
        Assert.Equal(1440, manual.Resolution);
        Assert.Equal(90, manual.Fps);
        Assert.Equal(30, manual.ReplaySeconds);
        Assert.Null(manual.TargetKbps);
    }

    [Fact]
    public void Native_and_auto_are_explicit_values_and_exact_encoder_does_not_fallback()
    {
        var global = Defaults();
        var automatic = RecordingVideoOptions.Resolve(global, new() { Aspect = "", ResolutionShortSide = 0, EncoderId = "" });
        Assert.Equal("", automatic.Aspect);
        Assert.Equal(0, automatic.Resolution);
        var available = new HashSet<string> { "hevc_nvenc", "h264_nvenc" };
        Assert.Equal("hevc_nvenc", automatic.SelectEncoder(available, false));
        var exact = RecordingVideoOptions.Resolve(global, new() { EncoderId = "av1_nvenc" });
        Assert.Null(exact.SelectEncoder(available, true));
        Assert.Equal("hevc_nvenc", exact.SelectEncoder(available, false));
        var codec = RecordingVideoOptions.Resolve(global, new() { Codec = GameRecordingCodec.H264 });
        Assert.Equal("h264_nvenc", codec.SelectEncoder(available, false));
        var legacy = RecordingVideoOptions.Resolve(global, new() { Aspect = "32:9", Bitrate = new(GameRateControl.Automatic) });
        Assert.Equal("32:9", legacy.Aspect);
        Assert.Null(legacy.TargetKbps);
        Assert.Throws<ArgumentException>(() => RecordingVideoOptions.Resolve(global, new() { Bitrate = new(GameRateControl.Cqp, Quantizer: 20) }));
    }

    [Fact]
    public void Exact_encoder_custom_size_and_bitrate_survive_profile_roundtrip()
    {
        var directory = Directory.CreateTempSubdirectory("quickclip-recording-video-options-");
        try
        {
            var store = new GameProfileStore(directory.FullName);
            var policy = new GameRecordingPolicy { Aspect = "32:9", ResolutionShortSide = 1000,
                EncoderId = "hevc_nvenc", Bitrate = new(GameRateControl.CustomBitrate, 10000) };
            var doc = new GameProfileDocument { Profiles = new GameProfileDocument().Profiles.Add("steam:1", policy) };
            store.Save(doc);
            var read = store.Load();
            Assert.Equal(GameProfileLoadStatus.Loaded, read.Status);
            Assert.Equal(policy, read.Document.Profiles["steam:1"]);
            var row = RecordingProfileSelection.Build([new CatalogEntry("steam:1", "Game", CatalogSource.Steam, [], [], new())], read.Document)[1];
            var reset = RecordingProfileSelection.Change(read.Document, row, policy with { ResolutionShortSide = null });
            store.Save(reset);
            var resolved = RecordingVideoOptions.Resolve(Defaults(), store.Load().Document.Profiles["steam:1"]);
            Assert.Equal(1440, resolved.Resolution);
            Assert.Equal("32:9", resolved.Aspect);
            Assert.Equal("hevc_nvenc", resolved.EncoderId);
            Assert.Equal(10000, resolved.TargetKbps);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void Stable_aliases_resolve_without_applying_one_games_settings_to_another()
    {
        var a = new CatalogEntry("steam:220", "Half-Life", CatalogSource.Steam, [], [], new()) { AliasIds = ["steam:380"] };
        var b = new CatalogEntry("steam:400", "Portal", CatalogSource.Steam, [], [], new());
        var policy = new GameRecordingPolicy { Fps = 30 };
        var doc = new GameProfileDocument
        {
            Profiles = new GameProfileDocument().Profiles.Add("steam:380", policy),
        };
        Assert.Equal(policy, RecordingVideoOptions.FindPolicy(a.Id, [a, b], doc));
        Assert.Equal(policy, RecordingVideoOptions.FindPolicy("steam:380", [a, b], doc));
        Assert.Null(RecordingVideoOptions.FindPolicy(b.Id, [a, b], doc));
        Assert.Null(RecordingVideoOptions.FindPolicy(null, [a, b], doc));
        var conflict = doc with { Profiles = doc.Profiles.Add(a.Id, new() { Fps = 60 }) };
        Assert.Throws<InvalidOperationException>(() => RecordingVideoOptions.FindPolicy(a.Id, [a, b], conflict));
    }
}
