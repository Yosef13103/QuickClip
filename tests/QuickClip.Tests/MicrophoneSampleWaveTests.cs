using System.Buffers.Binary;
using QuickClip.Recording;
using static QuickClip.Recording.CaptureEngine;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class MicrophoneSampleWaveTests
{
    [Fact]
    public void Stereo_sample_has_canonical_header_and_preserves_both_channels()
    {
        byte[] wave = MicrophoneSampleWave.Encode([short.MinValue, short.MaxValue, 0, -123]);
        Assert.Equal(52, wave.Length);
        Assert.Equal("RIFF"u8.ToArray(), wave[..4]);
        Assert.Equal(44, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)));
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)));
        Assert.Equal(4, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46)));
        Assert.Equal(-123, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(50)));
    }

    [Fact]
    public void Rejects_empty_partial_frame_or_over_five_seconds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MicrophoneSampleWave.Encode([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MicrophoneSampleWave.Encode([1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MicrophoneSampleWave.Encode(new short[MicrophoneSampleCapacity + 2]));
    }

    [Fact]
    public async Task Missing_endpoint_never_returns_a_playable_sample()
    {
        const string missing = "{0.0.1.00000000}.{00000000-0000-0000-0000-000000000000}";
        Assert.True(IsAvailable);
        long token = 0;
        int start = unchecked((int)0x800700AA);
        for (int i = 0; i < 100 && start == unchecked((int)0x800700AA); i++)
        {
            start = StartMicrophoneSample(missing, out token);
            if (start == unchecked((int)0x800700AA)) await Task.Delay(10);
        }
        Assert.Equal(0, start);
        var buffer = new short[MicrophoneSampleCapacity];
        try
        {
            int result = 0;
            for (int i = 0; i < 100 && result == 0; i++)
            {
                result = ReadMicrophoneSample(token, buffer);
                if (result == 0) await Task.Delay(10);
            }
            Assert.Equal(-1, result);
        }
        finally { StopMicrophoneTest(token); }
        Assert.Equal(-1, ReadMicrophoneSample(token, buffer));
    }
}
