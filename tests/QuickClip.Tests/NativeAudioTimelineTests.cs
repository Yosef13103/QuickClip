using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class NativeAudioTimelineTests
{
    [Fact]
    public void Restart_with_valid_backward_timestamp_does_not_mix_a_source_with_itself()
    {
        var output = Mix([100, 120, 130, 150], [0, 0, 0, 0], [1, 0, 1, 0]);
        Assert.Equal(-1, output.AsSpan(100 * 48, 70 * 48).IndexOfAnyExcept(0.75f));
    }

    [Fact]
    public void Backward_timestamps_without_restart_trim_overlap_until_the_source_catches_up()
    {
        var output = Mix([100, 120, 140, 160, 120, 140, 160, 180, 200], new int[9], new int[9]);
        Assert.Equal(-1, output.AsSpan(100 * 48, 120 * 48).IndexOfAnyExcept(0.75f));
    }

    [Fact]
    public void Independent_sources_still_sum_and_a_late_packet_keeps_its_undrained_tail()
    {
        var output = Mix([100, 110, 110], [0, 0, 1], [1, 1, 1], drainBeforePacket: 1, drainMs: 115);
        Assert.Equal(0.75f, output[112 * 48]);
        Assert.Equal(1.50f, output[117 * 48]);
        Assert.Equal(1.50f, output[125 * 48]);
        Assert.Equal(0, output[135 * 48]);
    }

    private static float[] Mix(int[] timestamps, int[] sources, int[] restarts, int drainBeforePacket = 0, int drainMs = 0)
    {
        Assert.True(CaptureEngine.IsAvailable);
        CaptureEngine.Initialize();
        var output = new float[48_000];
        Assert.Equal(output.Length, qc_test_audio_timeline(timestamps, sources, restarts, timestamps.Length,
            drainBeforePacket, drainMs, output, output.Length));
        return output;
    }

    [DllImport("QuickClipCapture.dll")]
    private static extern int qc_test_audio_timeline([In] int[] startsMs, [In] int[] sources, [In] int[] restarts,
        int packets, int drainBeforePacket, int drainMs, [Out] float[] left, int outputFrames);
}
