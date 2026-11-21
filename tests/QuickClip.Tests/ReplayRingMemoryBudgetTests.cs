using System.Runtime.InteropServices;

namespace QuickClip.Tests;

/// <summary>Exercises native AVPacket retention without opening a graphics device.</summary>
public sealed class ReplayRingMemoryBudgetTests
{
    public ReplayRingMemoryBudgetTests()
    {
        Assert.True(QuickClip.Recording.CaptureEngine.IsAvailable);
        QuickClip.Recording.CaptureEngine.Initialize(); // Initializes the FFmpeg dependency loader before direct test P/Invoke.
    }

    [DllImport("QuickClipCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_test_replay_ring_budget(int frames, int intervalUs,
        int keyInterval, int packetBytes, long byteBudget, out long retainedBytes,
        out long firstKeyUs, out int videoPackets, out int audioPackets);

    [Fact]
    public void Memory_pressure_evicts_old_packets_and_preserves_latest_decodable_keyframe()
    {
        Assert.True(QuickClip.Recording.CaptureEngine.IsAvailable);
        int success = qc_test_replay_ring_budget(8, 1_000_000, 3, 128, 600,
            out long bytes, out long firstKey, out int video, out int audio);
        Assert.Equal(1, success);
        Assert.InRange(bytes, 0, 600);
        Assert.Equal(6_000_000, firstKey);
        Assert.Equal(2, video);
        Assert.Equal(2, audio);
    }

    [Fact]
    public void Uncapped_ring_retains_all_requested_synthetic_frames()
    {
        int success = qc_test_replay_ring_budget(8, 1_000_000, 3, 128, 0,
            out long bytes, out long firstKey, out int video, out int audio);
        Assert.Equal(1, success);
        Assert.Equal(8 * 2 * 128, bytes);
        Assert.Equal(0, firstKey);
        Assert.Equal(8, video);
        Assert.Equal(8, audio);
    }

    [Fact]
    public void Oversized_packet_is_removed_instead_of_exceeding_the_budget()
    {
        int success = qc_test_replay_ring_budget(1, 1_000_000, 1, 4096, 128,
            out long bytes, out long firstKey, out int video, out int audio);
        Assert.Equal(1, success);
        Assert.Equal(0, bytes);
        Assert.Equal(-1, firstKey);
        Assert.Equal(0, video);
        Assert.Equal(0, audio);
    }

    [Fact]
    public void Invalid_budget_fixture_inputs_are_rejected()
    {
        Assert.Equal(0, qc_test_replay_ring_budget(8, 1_000_000, 3, 128, -1,
            out _, out _, out _, out _));
    }
}
