using System.Buffers.Binary;

namespace QuickClip.Recording;

/// <summary>Canonical, bounded PCM16 stereo WAV for explicit in-memory microphone preview.</summary>
internal static class MicrophoneSampleWave
{
    internal static byte[] Encode(ReadOnlySpan<short> samples)
    {
        if (samples.Length == 0 || samples.Length > CaptureEngine.MicrophoneSampleCapacity || samples.Length % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(samples));
        var wave = new byte[44 + samples.Length * 2];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 2);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 48000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 192000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), samples.Length * 2);
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + 2 * i), samples[i]);
        return wave;
    }
}
