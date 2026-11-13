using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace QuickClip.Shell;

/// <summary>Converts a local, short notification sound to canonical 48kHz 16-bit mono PCM in memory.</summary>
internal static class ClipSoundCustom
{
    internal const int MaxFileBytes = 16 * 1024 * 1024;
    internal const int SampleRate = 48_000;
    internal const int MaxSeconds = 3;
    internal const int MaxPcmBytes = SampleRate * MaxSeconds * sizeof(short);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".aiff", ".aif" };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string? _cachedPath;
    private static long _cachedLength;
    private static DateTime _cachedModified;
    private static byte[]? _cachedWav;

    internal static bool Supports(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) &&
        Path.IsPathFullyQualified(path) && Extensions.Contains(Path.GetExtension(path));

    internal static byte[] CanonicalWav(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length is < 2 or > MaxPcmBytes || (pcm.Length & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(pcm));
        byte[] wav = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wav.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8, 8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), 16);
        "data"u8.CopyTo(wav.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }

    internal static async Task<byte[]?> LoadAsync(string? path, CancellationToken ct = default)
    {
        if (!Supports(path) || Deps.FFmpeg == null) return null;
        try
        {
            var file = new FileInfo(path!);
            if (!file.Exists || file.Length is < 1 or > MaxFileBytes) return null;
            await Gate.WaitAsync(ct);
            try
            {
                // Decode only on change. Short PCM is cached, not the original potentially large file.
                if (string.Equals(_cachedPath, file.FullName, StringComparison.OrdinalIgnoreCase) &&
                    _cachedLength == file.Length && _cachedModified == file.LastWriteTimeUtc)
                    return _cachedWav;
                byte[]? wav = await DecodeAsync(file.FullName, ct);
                if (wav == null) return null;
                (_cachedPath, _cachedLength, _cachedModified, _cachedWav) =
                    (file.FullName, file.Length, file.LastWriteTimeUtc, wav);
                return wav;
            }
            finally { Gate.Release(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or OperationCanceledException)
        {
            Log.Write("Custom notification sound could not be loaded: " + ex.GetType().Name);
            return null;
        }
    }

    private static async Task<byte[]?> DecodeAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-protocol_whitelist", "file", "-i", path, "-map", "0:a:0",
            "-vn", "-sn", "-dn", "-t", MaxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ac", "1", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le", "-f", "s16le", "pipe:1",
        };
        using var process = new Process { StartInfo = Proc.StartInfo(Deps.FFmpeg!, args) };
        try
        {
            if (!process.Start()) return null;
            using var stop = timeout.Token.Register(() => Proc.KillQuietly(process));
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var pcm = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                int n = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token);
                if (n == 0) break;
                if (pcm.Length + n > MaxPcmBytes) return null;
                pcm.Write(buffer, 0, n);
            }
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                Log.Write("Custom notification sound failed FFmpeg decoding: " +
                    (await errors).Split('\n').FirstOrDefault());
                return null;
            }
            return pcm.Length >= 2 && (pcm.Length & 1) == 0 ? CanonicalWav(pcm.ToArray()) : null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or
            InvalidOperationException or OperationCanceledException)
        {
            Log.Write("Custom notification decode failed: " + ex.GetType().Name);
            return null;
        }
        finally { Proc.KillQuietly(process); }
    }
}
