using QuickClip.Gallery;
using QuickClip.Media;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class RecordingAudioMixTests
{
    private static readonly DateTimeOffset Captured = new(2026, 11, 22, 1, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void New_clip_snapshots_current_levels_for_the_actual_running_sources()
    {
        EndpointTrack[] active =
        [
            new(EndpointFlow.Microphone, "input-id", "Sonar - Microphone"),
            new(EndpointFlow.Playback, "game-id", "Sonar - Gaming"),
            new(EndpointFlow.Playback, "chat-id", "Chat"),
        ];
        EndpointTrack[] preferences =
        [
            new(EndpointFlow.Playback, "chat-id", "A renamed Chat", true, 45),
            new(EndpointFlow.Playback, "game-id", "Gaming", true, 100),
            new(EndpointFlow.Microphone, "input-id", "Mic", true, 85),
            new(EndpointFlow.Playback, "disabled-id", "Disabled", false, 5),
        ];
        var captured = RecordingAudioMix.Capture(active, preferences);
        Assert.Equal(new[]
        {
            new ClipAudioMixLevel(0, "Sonar - Microphone", 85),
            new ClipAudioMixLevel(1, "Sonar - Gaming", 100),
            new ClipAudioMixLevel(2, "Chat", 45),
        }, captured);
        // Saved mix is immutable with respect to later preferences.
        preferences[0] = preferences[0] with { DefaultMixPercent = 170 };
        var details = new ClipDetails(Captured, "Portal 2") { InitialAudioMix = captured };
        Assert.Equal(45, RecordingAudioMix.ForStream(details,
            new AudioStreamInfo { Ordinal = 2, Title = "Chat" }));
    }

    [Fact]
    public void Old_and_external_clips_have_no_snapshot_so_new_preferences_never_retroactively_apply()
    {
        var previous = new ClipDetails(Captured, "Terraria");
        Assert.Null(previous.InitialAudioMix);
        Assert.Null(RecordingAudioMix.ForStream(previous,
            new AudioStreamInfo { Ordinal = 0, Title = "Chat" }));
        Assert.Null(RecordingAudioMix.Capture([], [new(EndpointFlow.Playback, "chat", "Chat", true, 40)]));
    }

    [Fact]
    public void Preset_requires_original_stream_ordinal_and_title_to_prevent_misrouting()
    {
        var d = new ClipDetails(Captured, "Portal 2")
        {
            InitialAudioMix = [new(1, "Chat", 35)]
        };
        Assert.Equal(35, RecordingAudioMix.ForStream(d, new AudioStreamInfo { Ordinal = 1, Title = "chat" }));
        Assert.Null(RecordingAudioMix.ForStream(d, new AudioStreamInfo { Ordinal = 0, Title = "Chat" }));
        Assert.Null(RecordingAudioMix.ForStream(d, new AudioStreamInfo { Ordinal = 1, Title = "Gaming" }));
        Assert.Null(RecordingAudioMix.ForStream(d, new AudioStreamInfo { Ordinal = 1 }));
    }

    [Fact]
    public void Saved_preferences_clamp_to_safe_slider_range_and_older_settings_default_to_100()
    {
        var legacy = AppSettings.Parse("""
            {"UseAudioEndpoints":true,"EndpointTracks":[
              {"Flow":"Playback","DeviceId":"game","Label":"Game","Enabled":true},
              {"Flow":"Playback","DeviceId":"chat","Label":"Chat","Enabled":true,"DefaultMixPercent":500},
              {"Flow":"Microphone","DeviceId":"mic","Label":"Mic","Enabled":true,"DefaultMixPercent":-30}
            ]}
            """);
        Assert.Equal(new[] { 100, 200, 0 }, legacy.EndpointTracks.Select(x => x.DefaultMixPercent));
        var restored = AppSettings.Parse(legacy.ToJson());
        Assert.Equal(legacy.EndpointTracks, restored.EndpointTracks);
    }

    [Fact]
    public void Clip_metadata_preserves_and_validates_per_clip_mix_without_mutating_legacy()
    {
        var previous = new ClipDetails(Captured, "Portal 2");
        var serializedLegacy = System.Text.Json.JsonSerializer.Serialize(previous);
        Assert.DoesNotContain("InitialAudioMix", serializedLegacy);
        var restored = System.Text.Json.JsonSerializer.Deserialize<ClipDetails>(serializedLegacy)!;
        Assert.Null(restored.InitialAudioMix);

        var saved = new ClipDetails(Captured, "Portal 2")
        {
            InitialAudioMix =
            [
                new(0, "Gaming", 100),
                new(1, "Chat", 45),
                new(1, "Duplicate", 65),
                new(2, "Invalid", 400),
                new(-1, "Invalid index", 50),
            ]
        };
        var normal = saved.Normalize();
        Assert.Equal([new(0, "Gaming", 100), new(1, "Chat", 45)], normal.InitialAudioMix!);
        var json = System.Text.Json.JsonSerializer.Serialize(normal);
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<ClipDetails>(json)!;
        Assert.Equal(normal.InitialAudioMix!, reloaded.InitialAudioMix!);
        Assert.Equal(45, RecordingAudioMix.ForStream(reloaded,
            new AudioStreamInfo { Ordinal = 1, Title = "Chat" }));
    }
}
