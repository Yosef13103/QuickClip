using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

/// <summary>Detached native packet + codec lifetime, scoped epoch rejection, and quotas.
/// No GPU, authorized windows, microphones, or actual recordings are opened.</summary>
public sealed class DetachedEpochArchiveTests
{
    public DetachedEpochArchiveTests()
    {
        Assert.True(CaptureEngine.IsAvailable);
        CaptureEngine.Initialize();
    }

    [DllImport("QuickClipCapture.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int qc_test_detached_epoch_archive(
        int scenario, out int epochs, out long bytes,
        out long firstUs, out long lastUs, out int streams);

    [Theory]
    [InlineData(0, 2, 640, 1_000_000, 6_000_000)]
    [InlineData(1, 1, 384, 1_000_000, 3_000_000)]
    [InlineData(2, 1, 256, 5_000_000, 6_000_000)]
    [InlineData(3, 1, 384, 1_000_000, 3_000_000)]
    [InlineData(4, 1, 384, 1_000_000, 3_000_000)]
    [InlineData(5, 1, 384, 1_000_000, 3_000_000)]
    public void Archive_retains_encoded_packets_after_graph_ring_is_destroyed(
        int scenario, int expectedEpochs, long expectedBytes,
        long expectedFirstUs, long expectedLastUs)
    {
        Assert.Equal(1, qc_test_detached_epoch_archive(scenario,
            out int epochs, out long bytes, out long firstUs, out long lastUs, out int streams));
        Assert.Equal(expectedEpochs, epochs);
        Assert.Equal(expectedBytes, bytes);
        Assert.Equal(expectedFirstUs, firstUs);
        Assert.Equal(expectedLastUs, lastUs);
        Assert.Equal(2, streams);
    }

    [Fact]
    public void Invalid_synthetic_archive_requests_are_rejected()
    {
        Assert.Equal(0, qc_test_detached_epoch_archive(-1,
            out _, out _, out _, out _, out _));
    }
}
