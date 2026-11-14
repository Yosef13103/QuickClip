using System.Runtime.InteropServices;
using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Tests;

public sealed class MicrophoneSignalTests
{
    [Fact]
    public void Native_levels_and_mixer_distinguish_tone_zero_and_silent_buffers()
    {
        Assert.Equal((0f, 0f, 0L), Measure(null, 4));
        Assert.Equal((0f, 0f, 0L), Measure([0f, 0f, 0f, 0f], 4));
        Assert.Equal((0.5f, 0.5f, 1L), Measure([0.5f, -0.5f, -0.5f, 0.5f], 4));
        var mixed = Measure([0.5f, float.NaN, float.PositiveInfinity, -0.5f], 4);
        Assert.Equal(0.5f, mixed.Peak);
        Assert.Equal(MathF.Sqrt(0.125f), mixed.Rms, 6);
        Assert.Equal(1, mixed.MixerBlocks);
    }

    [Fact]
    public void Matching_checks_endpoint_identity_and_flow_including_a_resolved_default()
    {
        var capture = new NativeAudioSignal { Flow = EndpointFlow.Microphone, SelectedId = "mic-a" };
        Assert.True(MicrophoneSignal.Matches(capture, "MIC-A"));
        Assert.False(MicrophoneSignal.Matches(capture, "mic-b"));
        capture.Flow = EndpointFlow.Playback;
        Assert.False(MicrophoneSignal.Matches(capture, "mic-a"));
        capture.Flow = EndpointFlow.Microphone;
        capture.SelectedId = "";
        capture.ResolvedId = "mic-a";
        capture.ActualFlow = (int)EndpointFlow.Playback;
        Assert.False(MicrophoneSignal.Matches(capture, "mic-a"));
        capture.ActualFlow = (int)EndpointFlow.Microphone;
        Assert.True(MicrophoneSignal.Matches(capture, "mic-a"));
        Assert.True(MicrophoneSignal.Matches(capture, ""));
    }

    [Fact]
    public void Meter_and_status_distinguish_missing_packets_silence_and_failures()
    {
        Assert.Equal(0, MicrophoneSignal.MeterValue(float.NaN));
        Assert.Equal(0, MicrophoneSignal.MeterValue(0.001f));
        Assert.True(MicrophoneSignal.MeterValue(0.1f) > MicrophoneSignal.MeterValue(0.01f));
        Assert.Equal(1, MicrophoneSignal.MeterValue(1.2f));

        var signal = new NativeAudioSignal { Stage = EndpointStage.Streaming, MillisecondsSincePacket = -1 };
        Assert.Equal("No recent audio packets", MicrophoneSignal.Describe(signal));
        signal.MillisecondsSincePacket = 5;
        Assert.Equal("No audible signal", MicrophoneSignal.Describe(signal));
        signal.WindowsPeak = 0.1f;
        Assert.Contains("QuickClip PCM is quiet", MicrophoneSignal.Describe(signal));
        signal.RecentPeak = 0.2f;
        Assert.Equal("Signal detected", MicrophoneSignal.Describe(signal));
        signal.HResult = unchecked((int)0x88890008);
        signal.Stage = EndpointStage.Initialize;
        Assert.Equal("WASAPI initialization failed (0x88890008)", MicrophoneSignal.Describe(signal));
    }

    [Fact]
    public async Task Native_missing_endpoint_reports_lookup_failure_and_test_handles_can_be_replaced()
    {
        const string missing = "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}";
        Assert.True(IsAvailable);
        Assert.Equal(unchecked((int)0x80070057), StartMicrophoneTest(missing, out _, durationMs: 10001));
        Assert.Equal(0, StartMicrophoneTest(missing, out long first, durationMs: 1000));
        try
        {
            var failed = await WaitForLookupFailure(first);
            Assert.Equal(EndpointFlow.Microphone, failed.Flow);
            Assert.Equal(EndpointStage.EndpointLookup, failed.Stage);
            Assert.Equal(missing, failed.SelectedId);
            Assert.Equal("", failed.ResolvedId);
            Assert.Equal(48_000, failed.RequestedRate);
            Assert.Equal(2, failed.RequestedChannels);
            Assert.Equal(0, failed.CaptureChannels);
            Assert.Equal(0, failed.PacketCount);
        }
        finally { StopMicrophoneTest(first); }
        Assert.Null(ReadMicrophoneTest(first));

        long second = 0;
        for (int i = 0; i < 100 && second == 0; i++)
        {
            if (StartMicrophoneTest(missing, out second, durationMs: 1000) == 0) break;
            await Task.Delay(10);
        }
        Assert.NotEqual(0, second);
        try
        {
            StopMicrophoneTest(first);
            Assert.Equal(EndpointStage.EndpointLookup, (await WaitForLookupFailure(second)).Stage);
        }
        finally { StopMicrophoneTest(second); }
        Assert.Null(ReadMicrophoneTest(second));
    }

    private static async Task<NativeAudioSignal> WaitForLookupFailure(long token)
    {
        for (int i = 0; i < 100; i++)
        {
            if (ReadMicrophoneTest(token) is { HResult: < 0 } signal) return signal;
            await Task.Delay(10);
        }
        throw new TimeoutException("The missing-endpoint probe did not report its failure.");
    }

    private static (float Peak, float Rms, long MixerBlocks) Measure(float[]? samples, int count)
    {
        Assert.Equal(1, qc_test_audio_levels(samples, count, out float peak, out float rms, out long mixerBlocks));
        return (peak, rms, mixerBlocks);
    }

    [DllImport("QuickClipCapture.dll")]
    private static extern int qc_test_audio_levels([In] float[]? samples, int count, out float peak, out float rms, out long mixerBlocks);
}
