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
    public void Ten_second_startup_check_latches_each_lane_independently()
    {
        var indicator = new MicrophoneIndicatorSignal();
        Assert.Equal(MicrophoneIndicatorState.Hidden, indicator.Observe([], 0, 0));
        indicator.Configure(1, ["a", "b"], 100);
        Assert.Equal(MicrophoneIndicatorState.Checking, indicator.Observe([Mic("a"), Mic("b", 0)], 2, 101));
        Assert.Equal(MicrophoneIndicatorState.Checking, indicator.Observe([], 0, 10099));
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([], 0, 10100));
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("b")], 1, 15000));
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([], 0, 1000000));
    }

    [Fact]
    public void Missing_failed_wrong_flow_and_stale_lanes_do_not_confirm()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["a", "b"], 0);
        var failed = Mic("b");
        failed.HResult = -1;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 10000));
        failed = Mic("b");
        failed.Flow = EndpointFlow.Playback;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 10001));
        failed = Mic("b");
        failed.MillisecondsSincePacket = 600;
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a"), failed], 2, 10002));
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("b")], 1, 10003));
    }

    [Fact]
    public void Epoch_change_and_selection_change_discard_previous_audio_but_repeated_refresh_does_not()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["a"], 0);
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([Mic("a")], 1, 1));
        indicator.Configure(1, ["a"], 2);
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([], 0, 50000));
        indicator.Configure(2, ["a"], 50001);
        Assert.Equal(MicrophoneIndicatorState.Checking, indicator.Observe([Mic("a", 0)], 1, 50002));
        indicator.Configure(2, ["b"], 50003);
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([Mic("a")], 1, 60003));
        indicator.Reset();
        Assert.Equal(MicrophoneIndicatorState.Hidden, indicator.State);
    }

    [Fact]
    public void Default_alias_can_share_a_proven_actual_default_lane_but_not_an_unrelated_lane()
    {
        var indicator = new MicrophoneIndicatorSignal();
        indicator.Configure(1, ["", "a"], 0);
        var lane = Mic("a");
        lane.DefaultCaptureId = "b";
        Assert.Equal(MicrophoneIndicatorState.Quiet, indicator.Observe([lane], 1, 10000));
        lane.DefaultCaptureId = "a";
        Assert.Equal(MicrophoneIndicatorState.Active, indicator.Observe([lane], 1, 10001));
    }

    [Fact]
    public void Only_enabled_microphones_in_the_selected_audio_mode_are_expected()
    {
        var settings = new AppSettings { RecordMic = true, MicDeviceIds = ["legacy"] };
        Assert.Equal(AppProfile.IsPersonal, settings.ShowGameRecordingIndicator);
        Assert.Equal(new[] { "legacy" }, MicrophoneIndicatorSignal.SelectedMicrophones(settings));
        settings.UseAudioEndpoints = true;
        settings.EndpointTracks = [new(EndpointFlow.Playback, "speaker", "Game"), new(EndpointFlow.Microphone, "a", "Mic"), new(EndpointFlow.Microphone, "b", "Muted", false)];
        Assert.Equal(new[] { "a" }, MicrophoneIndicatorSignal.SelectedMicrophones(settings));
        settings.ShowRecordingMicrophoneIndicator = false;
        Assert.Equal(new[] { "a" }, MicrophoneIndicatorSignal.SelectedMicrophones(settings));
    }
}
