using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Tests;

public sealed class SurroundLoopbackTests
{
    private const uint Surround51 = 0x003f; // FL, FR, FC, LFE, BL, BR
    private const uint Surround71 = 0x063f; // 5.1 plus SL and SR
    private const float SurroundGain = 0.70710678f;

    [Theory]
    [InlineData(0, 1, 0x4)]
    [InlineData(0, 2, 0x3)]
    [InlineData(0, 6, 0x0)]
    [InlineData(0, 6, 0x3)]
    [InlineData(1, 6, Surround51)]
    [InlineData(1, 8, Surround71)]
    public void Mono_stereo_unmapped_playback_and_capture_endpoints_keep_the_existing_conversion(int flow, int channels, uint mask)
    {
        CaptureEngine.Initialize();
        Assert.Equal(0, qc_test_loopback_downmix(flow, new float[channels], channels, mask, new float[2]));
    }

    [Theory]
    [InlineData(6, Surround51)]
    [InlineData(8, Surround71)]
    public void Front_stereo_keeps_unity_gain_and_surround_channels_keep_their_side(int channels, uint mask)
    {
        float[][] expected =
        [
            [1, 0], [0, 1], [SurroundGain, SurroundGain], [0.5f, 0.5f],
            [SurroundGain, 0], [0, SurroundGain], [SurroundGain, 0], [0, SurroundGain],
        ];
        for (int channel = 0; channel < channels; ++channel)
        {
            var samples = new float[channels];
            samples[channel] = 1;
            Assert.Equal(expected[channel], Mix(samples, mask));
        }
    }

    [Fact]
    public void Full_scale_71_sum_preserves_balance_without_clipping_and_ignores_nonfinite_samples()
    {
        var sum = Mix([1, 0, 1, 1, 1, 1, 1, 1], Surround71);
        Assert.Equal(1, sum[0]);
        Assert.Equal((0.5f + 3 * SurroundGain) / (1.5f + 3 * SurroundGain), sum[1], 6);
        Assert.Equal([-1f, 1f], Mix([-1, 1, float.NaN, float.PositiveInfinity, 0, 0], Surround51));
        Assert.Equal([0f, 0f], Mix(new float[8], Surround71));
    }

    private static float[] Mix(float[] samples, uint mask)
    {
        CaptureEngine.Initialize();
        var stereo = new float[2];
        Assert.Equal(1, qc_test_loopback_downmix(0, samples, samples.Length, mask, stereo));
        return stereo;
    }

    [DllImport("QuickClipCapture.dll")]
    private static extern int qc_test_loopback_downmix(int flow, [In] float[] input, int channels, uint mask, [Out] float[] stereo);
}
