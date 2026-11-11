using System.Collections.Immutable;
using QuickClip.Discovery;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class GameRecordingProfileTests : IDisposable
{
    private readonly DirectoryInfo _fixture = Directory.CreateTempSubdirectory("quickclip-game-profiles-");

    [Fact]
    public void Game_values_override_global_values_and_clears_have_explicit_meanings()
    {
        var defaults = new GameRecordingPolicy
        {
            Target = new(GameCaptureSource.GameMonitor), ReplaySeconds = 90, Fps = 60, Aspect = "16:9",
            AudioEndpoints = [new(EndpointFlow.Playback, "playback")], MicrophoneIds = ["mic"],
            OpenGalleryOnSessionEnd = true, Tags = ["global"], DefaultLabel = "Global",
            SaveShortcut = new(true, 2, 0x78)
        };
        var policy = new GameRecordingPolicy
        {
            Target = new(GameCaptureSource.ExplicitMonitor, "monitor-2"), Fallback = GameCaptureFallback.SkipRecording,
            ReplaySeconds = 30, SaveSeconds = 15, Aspect = "", AudioEndpoints = [], MicrophoneIds = [],
            OpenGalleryOnSessionEnd = false, Tags = [], DefaultLabel = "", SaveShortcut = new(false)
        };
        var resolved = policy.ResolveAgainst(defaults);

        Assert.Equal(policy.Target, resolved.Target);
        Assert.Equal(GameCaptureFallback.SkipRecording, resolved.Fallback);
        Assert.Equal(30, resolved.ReplaySeconds);
        Assert.Equal(15, resolved.SaveSeconds);
        Assert.Equal(60, resolved.Fps);
        Assert.Equal("", resolved.Aspect);
        Assert.Empty(resolved.AudioEndpoints!.Value);
        Assert.Empty(resolved.MicrophoneIds!.Value);
        Assert.Empty(resolved.Tags!.Value);
        Assert.Equal("", resolved.DefaultLabel);
        Assert.False(resolved.OpenGalleryOnSessionEnd);
        Assert.False(resolved.FullSessionRecording);
        Assert.False(resolved.BurstSave);
        Assert.False(resolved.SaveShortcut!.Enabled);
        Assert.Equal(defaults.Target, (policy with { Target = null }).ResolveAgainst(defaults).Target);
        Assert.Equal(defaults.AudioEndpoints, (policy with { AudioEndpoints = null }).ResolveAgainst(defaults).AudioEndpoints);
        Assert.Equal(defaults.Target, new GameProfileDocument().Resolve("unknown", defaults).Target);
    }

    [Fact]
    public void Effective_policy_validates_combinations_that_cross_the_inheritance_boundary()
    {
        var defaults = new GameRecordingPolicy { ReplaySeconds = 60, SaveSeconds = 45 };
        Assert.Throws<ArgumentException>(() => new GameRecordingPolicy { ReplaySeconds = 30 }.ResolveAgainst(defaults));
        Assert.Equal(15, new GameRecordingPolicy { ReplaySeconds = 30, SaveSeconds = 15 }.ResolveAgainst(defaults).SaveSeconds);
    }

    [Fact]
    public void Persists_policy_and_isolates_two_portable_settings_directories()
    {
        var first = new GameProfileStore(Path.Combine(_fixture.FullName, "portable-one", "settings"));
        var second = new GameProfileStore(Path.Combine(_fixture.FullName, "portable-two", "settings"));
        Assert.Equal(Path.Combine(AppProfile.SettingsDirectory, "game-recording-profiles.json"), GameProfileStore.ForCurrentProfile().FilePath);
        Assert.Equal(GameProfileLoadStatus.Missing, second.Load().Status);

        var policy = new GameRecordingPolicy
        {
            Target = new(GameCaptureSource.GameWindow), Fallback = GameCaptureFallback.GameMonitor,
            ReplaySeconds = 120, SaveSeconds = 30, ResolutionShortSide = 1440, Fps = 120, Aspect = "21:9",
            Codec = GameRecordingCodec.Hevc, Quality = RecordingQuality.Ultra, NvencPreset = CaptureEngine.NvencSpeed.FasterP3,
            Bitrate = new(GameRateControl.Vbr, 24000), AudioEndpoints = [new(EndpointFlow.Microphone, "endpoint-1")],
            MicrophoneIds = [""], Tags = ["Boss"], DefaultLabel = "Session", OpenGalleryOnSessionEnd = true,
            FullSessionRecording = true, SaveShortcut = new(true, 3, 0x78), BurstSave = true
        };
        first.Save(Document(policy));
        var loaded = first.Load();
        Assert.Equal(GameProfileLoadStatus.Loaded, loaded.Status);
        var restored = loaded.Document.Profiles["profile-a"];
        Assert.Equal(policy, restored with { AudioEndpoints = policy.AudioEndpoints, MicrophoneIds = policy.MicrophoneIds, Tags = policy.Tags });
        Assert.Equal(policy.AudioEndpoints!.Value.ToArray(), restored.AudioEndpoints!.Value.ToArray());
        Assert.Equal(policy.MicrophoneIds!.Value.ToArray(), restored.MicrophoneIds!.Value.ToArray());
        Assert.Equal(policy.Tags!.Value.ToArray(), restored.Tags!.Value.ToArray());

        first.Save(Document(policy with { Bitrate = new(GameRateControl.Cqp, Quantizer: 18), Tags = [], Fps = null }));
        Assert.Equal(18, first.Load().Document.Profiles["profile-a"].Bitrate!.Quantizer);
        Assert.Empty(first.Load().Document.Profiles["profile-a"].Tags!.Value);
        Assert.Null(first.Load().Document.Profiles["profile-a"].Fps);
        Assert.Equal(GameProfileLoadStatus.Missing, second.Load().Status);
    }

    [Fact]
    public void Imports_version_one_without_writing_until_explicit_save()
    {
        var store = new GameProfileStore(_fixture.FullName);
        const string draft = """{"schemaVersion":1,"profiles":{"profile-a":{"bufferSeconds":90,"fps":60}}}""";
        File.WriteAllText(store.FilePath, draft);
        var loaded = store.Load();
        Assert.Equal(GameProfileLoadStatus.Migrated, loaded.Status);
        Assert.Equal(GameProfileDocument.CurrentVersion, loaded.Document.SchemaVersion);
        Assert.Equal(90, loaded.Document.Profiles["profile-a"].ReplaySeconds);
        Assert.Equal(draft, File.ReadAllText(store.FilePath));
        store.Save(loaded.Document);
        Assert.Equal(GameProfileLoadStatus.Loaded, store.Load().Status);
        Assert.DoesNotContain("bufferSeconds", File.ReadAllText(store.FilePath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"schemaVersion":"2","profiles":{}}""")]
    [InlineData("""{"schemaVersion":2,"profiles":null}""")]
    [InlineData("""{"schemaVersion":2,"profiles":{"profile-a":null}}""")]
    [InlineData("""{"schemaVersion":2,"profiles":{"profile-a":{"fps":1}}}""")]
    [InlineData("""{"schemaVersion":2,"profiles":{"profile-a":{"codec":99}}}""")]
    [InlineData("""{"schemaVersion":2,"profiles":{"profile-a":{"audioEndpoints":[null]}}}""")]
    [InlineData("""{"schemaVersion":1,"profiles":{"profile-a":{"bufferSeconds":30,"replaySeconds":60}}}""")]
    public void Invalid_files_fall_back_to_global_policy_and_keep_original_bytes(string json)
    {
        var store = new GameProfileStore(_fixture.FullName);
        File.WriteAllText(store.FilePath, json);
        var result = store.Load();
        Assert.Equal(GameProfileLoadStatus.Invalid, result.Status);
        Assert.Equal(60, result.Document.Resolve("profile-a", new() { ReplaySeconds = 60 }).ReplaySeconds);
        Assert.Equal(json, File.ReadAllText(store.FilePath));
    }

    [Fact]
    public void Future_schema_is_preserved_and_oversized_documents_are_rejected()
    {
        var store = new GameProfileStore(_fixture.FullName);
        const string future = """{"schemaVersion":99,"profiles":{"unknown":42}}""";
        File.WriteAllText(store.FilePath, future);
        Assert.Equal(GameProfileLoadStatus.UnsupportedVersion, store.Load().Status);
        Assert.Equal(future, File.ReadAllText(store.FilePath));
        using (var oversized = File.Create(store.FilePath)) oversized.SetLength(1024 * 1024 + 1);
        Assert.Equal(GameProfileLoadStatus.Invalid, store.Load().Status);
    }

    [Fact]
    public void Invalid_overrides_cannot_replace_a_saved_profile()
    {
        var store = new GameProfileStore(_fixture.FullName);
        store.Save(Document(new() { Fps = 60 }));
        string original = File.ReadAllText(store.FilePath);
        GameRecordingPolicy[] invalid =
        [
            new() { Target = new(GameCaptureSource.ExplicitMonitor) },
            new() { Target = new(GameCaptureSource.GameWindow, "monitor-2") },
            new() { ReplaySeconds = 301 },
            new() { ResolutionShortSide = -1 },
            new() { Aspect = "bad" },
            new() { Bitrate = new(GameRateControl.Cqp, Kbps: 20000, Quantizer: 18) },
            new() { Bitrate = new(GameRateControl.Cbr, Kbps: 1) },
            new() { AudioEndpoints = [new(EndpointFlow.Playback, "id"), new(EndpointFlow.Microphone, "ID")] },
            new() { MicrophoneIds = ["id", "ID"] },
            new() { SaveShortcut = new(true, 0x20, 0x78) },
            new() { DefaultLabel = "bad\nlabel" }
        ];
        foreach (var policy in invalid) Assert.Throws<ArgumentException>(() => store.Save(Document(policy)));
        Assert.Equal(original, File.ReadAllText(store.FilePath));
    }

    private static GameProfileDocument Document(GameRecordingPolicy policy) => new()
    {
        Profiles = ImmutableDictionary<string, GameRecordingPolicy>.Empty.Add("profile-a", policy)
    };

    public void Dispose() => _fixture.Delete(recursive: true);
}
