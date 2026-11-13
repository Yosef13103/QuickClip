using System.Buffers.Binary;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class CustomNotificationSoundTests
{
    [Fact]
    public void Canonical_wave_has_a_valid_short_pcm_header_and_preserves_samples()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        byte[] wav = ClipSoundCustom.CanonicalWav(pcm);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVEfmt "u8.ToArray(), wav[8..16]);
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4, 4)));
        Assert.Equal(ClipSoundCustom.SampleRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4)));
        Assert.Equal(ClipSoundCustom.SampleRate * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28, 4)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4)));
        Assert.Equal(pcm, wav[44..]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipSoundCustom.CanonicalWav(new byte[1]));
    }

    [Fact]
    public void Only_local_whitelisted_audio_paths_can_be_selected()
    {
        Assert.True(ClipSoundCustom.Supports(@"C:\Sounds\nice.ogg"));
        Assert.True(ClipSoundCustom.Supports(@"C:\Sounds\nice.mp3"));
        Assert.False(ClipSoundCustom.Supports(@"C:\Sounds\not-audio.exe"));
        Assert.False(ClipSoundCustom.Supports("https://example.invalid/sound.wav"));
        Assert.False(ClipSoundCustom.Supports(@"\\server\share\sound.wav"));
        Assert.False(ClipSoundCustom.Supports("relative.wav"));
    }

    [Fact]
    public void Selected_sound_setting_round_trips_without_changing_default()
    {
        const string path = @"C:\My Clips\my chime.ogg";
        var settings = AppSettings.Parse(new AppSettings { ClipSoundFilePath = path }.ToJson());
        Assert.Equal(path, settings.ClipSoundFilePath);
        Assert.Null(AppSettings.Parse("{}").ClipSoundFilePath);
    }

    [Fact]
    public async Task Bundled_pcm_can_be_decoded_as_custom_audio_without_writing_a_conversion_file()
    {
        if (Deps.FFmpeg == null) return;
        var folder = Directory.CreateTempSubdirectory("qc-custom-sound-");
        try
        {
            string path = Path.Combine(folder.FullName, "example.wav");
            File.WriteAllBytes(path, ClipSound.Load());
            byte[] decoded = Assert.IsType<byte[]>(await ClipSoundCustom.LoadAsync(path));
            Assert.InRange(decoded.Length, 46, 44 + ClipSoundCustom.MaxPcmBytes);
            Assert.Equal(1, BitConverter.ToInt16(decoded, 20));  // PCM
            Assert.Equal(1, BitConverter.ToInt16(decoded, 22));  // mono
            Assert.Equal(16, BitConverter.ToInt16(decoded, 34));
            Assert.Same(decoded, await ClipSoundCustom.LoadAsync(path)); // cached until file changes
            Assert.Equal(decoded.Length, ClipSoundOutput.ScalePcm(decoded, 100).Length);
        }
        finally { folder.Delete(recursive: true); }
    }
}
