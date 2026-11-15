using QuickClip.Recording;
using QuickClip.Shell;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Tests;

public sealed class MicrophoneIndicatorSignalTests
{
    private static NativeAudioSignal Mic(string id, float peak = .2f) => new()
    {
        Flow = EndpointFlow.Microphone, ActualFlow = (int)EndpointFlow.Microphone,
        SelectedId = id, ResolvedId = id, Stage = EndpointStage.Streaming,
        PacketCount = 1, MillisecondsSincePacket = 5, RecentPeak = peak,
    };

    [Fact]
    public void No_selection_is_hidden_and_every_selected_lane_needs_recent_pcm()
    {
        var indicator = new MicrophoneIndicatorSignal();
        Assert.Equal(MicrophoneIndicatorState.Hidden, indicator.Observe([], 0, 0));
        indicator.Configure(1, ["a", "b"]);
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), Mic("b", 0)], 2, 0));
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("a", 0), Mic("b")], 2, 100));
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("a", 0), Mic("b", 0)], 2, 500));
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a", 0), Mic("b")], 2, 2501));
    }

    [Fact]
    public void Missing_failed_wrong_flow_and_stale_lanes_do_not_count()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["a", "b"]);
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("a"), Mic("b")], 2, 0));
        var failed = Mic("b");
        failed.HResult = -1;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 1));
        failed = Mic("b");
        failed.Flow = EndpointFlow.Playback;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 2));
        failed = Mic("b");
        failed.MillisecondsSincePacket = 600;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 3));
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a")], 1, 4));
    }

    [Fact]
    public void Epoch_change_and_selection_change_discard_previous_audio()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["a"]);
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("a")], 1, 1));
        indicator.Configure(2, ["a"]);
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a", 0)], 1, 2));
        indicator.Configure(2, ["b"]);
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a")], 1, 3));
        indicator.Reset();
        Assert.Equal(MicrophoneIndicatorState.Hidden, indicator.State);
    }

    [Fact]
    public void Default_alias_can_share_a_proven_actual_default_lane_but_not_an_unrelated_lane()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["", "a"]);
        var lane = Mic("a");
        lane.DefaultCaptureId = "b";
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([lane], 1, 0));
        lane.DefaultCaptureId = "a";
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([lane], 1, 1));
    }

    [Fact]
    public void Only_enabled_microphones_in_the_selected_audio_mode_are_expected()
    {
        var settings = new AppSettings { RecordMic = true, MicDeviceIds = ["legacy"] };
        Assert.False(settings.ShowGameRecordingIndicator);
        Assert.Equal(new[] { "legacy" }, MicrophoneIndicatorSignal.SelectedMicrophones(settings));
        settings.UseAudioEndpoints = true;
        settings.EndpointTracks = [new(EndpointFlow.Playback, "speaker", "Game"), new(EndpointFlow.Microphone, "a", "Mic"), new(EndpointFlow.Microphone, "b", "Muted", false)];
        Assert.Equal(new[] { "a" }, MicrophoneIndicatorSignal.SelectedMicrophones(settings));
        settings.ShowRecordingMicrophoneIndicator = false;
        Assert.Empty(MicrophoneIndicatorSignal.SelectedMicrophones(settings));
    }
}
