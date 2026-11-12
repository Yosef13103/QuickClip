using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class EndpointAudioTests
{
    [Fact]
    public void Older_settings_keep_legacy_audio_and_empty_explicit_mode_is_video_only()
    {
        var settings = AppSettings.Parse("""{ "RecordMic": true, "MicDeviceId": "default-mic" }""");
        Assert.Null(EndpointAudio.Plan(settings));
        Assert.Equal(2, RecordingPresets.AudioTrackCount(settings));

        settings.UseAudioEndpoints = true;
        Assert.Empty(EndpointAudio.Plan(settings)!);
        Assert.Equal(0, RecordingPresets.AudioTrackCount(settings));
        Assert.Equal(["default-mic"], settings.MicDeviceIds);
    }

    [Fact]
    public void Endpoint_settings_round_trip_with_names_order_and_missing_device_ids()
    {
        var original = new AppSettings
        {
            UseAudioEndpoints = true,
            EndpointTracks =
            [
                new(EndpointFlow.Playback, "render-a", "Game"),
                new(EndpointFlow.Playback, "render-b", "Chat", false),
                new(EndpointFlow.Microphone, "unplugged-capture", "Mic"),
            ],
        };
        var loaded = AppSettings.Parse(original.ToJson());
        Assert.Equal(original.EndpointTracks, loaded.EndpointTracks);
        Assert.Equal(["render-a", "unplugged-capture"], EndpointAudio.Plan(loaded)!.Select(t => t.DeviceId));
        Assert.Equal(["Game", "Mic"], EndpointAudio.Plan(loaded)!.Select(t => t.Label));
        Assert.Equal(2, RecordingPresets.AudioTrackCount(loaded));
    }

    [Fact]
    public void Endpoint_plan_prevents_duplicates_and_gives_tracks_distinct_titles()
    {
        var settings = new AppSettings
        {
            UseAudioEndpoints = true,
            EndpointTracks =
            [
                new(EndpointFlow.Playback, "render-a", "  Game\0  "),
                new(EndpointFlow.Playback, "RENDER-A", "Duplicate"),
                new(EndpointFlow.Playback, "render-b", "game"),
                new(EndpointFlow.Microphone, "input", " "),
                new((EndpointFlow)50, "wrong-flow", "Invalid"),
                new(EndpointFlow.Microphone, "", "Default is not an explicit ID"),
            ],
        };
        Assert.Equal(["render-a", "render-b", "input"], EndpointAudio.Plan(settings)!.Select(t => t.DeviceId));
        Assert.Equal(["Game", "game (2)", "Mic"], EndpointAudio.Plan(settings)!.Select(t => t.Label));
    }

    [Fact]
    public void Endpoint_limit_matches_the_native_configuration()
    {
        var settings = new AppSettings
        {
            UseAudioEndpoints = true,
            EndpointTracks = Enumerable.Range(0, 20).Select(i => new EndpointTrack(EndpointFlow.Playback, $"id-{i}", $"Track {i}")).ToList(),
        };
        Assert.Equal(EndpointAudio.MaxTracks, EndpointAudio.Plan(settings)!.Count);
        Assert.Empty(AppSettings.Parse("""{ "EndpointTracks": [null] }""").EndpointTracks);
    }

    [Fact]
    public void Same_friendly_name_in_playback_and_input_stays_distinct()
    {
        AudioEndpoint render = new(EndpointFlow.Playback, "render-fixture", "Virtual Microphone", true);
        AudioEndpoint capture = new(EndpointFlow.Microphone, "input-fixture", "Virtual Microphone", true);
        AudioEndpoint[] endpoints = [render, capture];
        var game = new EndpointTrack(EndpointFlow.Playback, render.Id, "Game");
        var mic = new EndpointTrack(EndpointFlow.Microphone, capture.Id, "Mic");
        Assert.Equal([render], EndpointAudio.ForFlow(endpoints, EndpointFlow.Playback));
        Assert.Equal([capture], EndpointAudio.ForFlow(endpoints, EndpointFlow.Microphone));
        Assert.Same(render, EndpointAudio.FindEndpoint(game, endpoints));
        Assert.Same(capture, EndpointAudio.FindEndpoint(mic, endpoints));
        Assert.Null(EndpointAudio.FindEndpoint(mic with { DeviceId = render.Id }, endpoints));
        Assert.Null(EndpointAudio.FindEndpoint(mic, [render]));
        Assert.Equal([game, mic], EndpointAudio.Normalize([game, mic]));
    }

    [Fact]
    public void Native_peak_uses_synthetic_samples_and_handles_silence_without_an_endpoint()
    {
        Assert.True(CaptureEngine.IsAvailable);
        CaptureEngine.Initialize();
        Assert.Equal(0f, qc_test_audio_packet_peak(null, 4));
        Assert.Equal(0f, qc_test_audio_packet_peak([0f, 0f, 0f], 3));
        Assert.Equal(0.625f, qc_test_audio_packet_peak([0f, -0.625f, 0.25f, float.NaN, float.PositiveInfinity], 5));
    }

    [DllImport("QuickClipCapture.dll")]
    private static extern float qc_test_audio_packet_peak([In] float[]? samples, int count);
}
