using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace QuickClip;

/// <summary>Locates the bundled FFmpeg/FFprobe/libmpv binaries.</summary>
internal static class Deps
{
    public static string AppDir { get; } = AppContext.BaseDirectory;
    public static string? FFmpeg { get; } = Find("ffmpeg.exe");
    public static string? FFprobe { get; } = Find("ffprobe.exe");
    public static bool HasLibMpv => File.Exists(Path.Combine(AppDir, "libmpv-2.dll"));

    private static string? Find(string exe)
    {
        foreach (var dir in new[] { Path.Combine(AppDir, "ffmpeg"), AppDir })
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}

internal static class Proc
{
    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct = default)
    {
        using var p = Process.Start(StartInfo(exe, args)) ?? throw new InvalidOperationException($"Could not start {exe}");
        using var reg = ct.Register(() => KillQuietly(p));
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, await stdout, await stderr);
    }

    public static void KillQuietly(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

}

internal static class Fmt
{
    public static string Num(double v, string format = "0.###") => v.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>1:02:03.456 / 2:03.456</summary>
    public static string Time(double seconds, bool millis = true)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        string ms = millis ? "." + t.Milliseconds.ToString("000") : "";
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{ms}"
            : $"{t.Minutes}:{t.Seconds:00}{ms}";
    }

    /// <summary>Whole seconds for short UI labels, independent of media or trimming precision.</summary>
    public static string CompactDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= long.MaxValue) return "00";
        long wholeSeconds = (long)seconds;
        return wholeSeconds < 60 ? wholeSeconds.ToString("00", CultureInfo.InvariantCulture)
            : FormattableString.Invariant($"{wholeSeconds / 60:00}:{wholeSeconds % 60:00}");
    }

    /// <summary>Human-readable duration for recording status and save confirmations.</summary>
    public static string FriendlyDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= long.MaxValue) return "0 seconds";
        long n = (long)seconds;
        long hours = n / 3600, minutes = n / 60 % 60, remainder = n % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours} hour{(hours == 1 ? "" : "s")}");
        if (minutes > 0) parts.Add($"{minutes} minute{(minutes == 1 ? "" : "s")}");
        if (remainder > 0 || parts.Count == 0)
            parts.Add($"{remainder} second{(remainder == 1 ? "" : "s")}");
        return string.Join(" ", parts);
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Parses "1:23.5", "83.5", "1:02:03" into seconds.</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is 0 or > 3) return false;
        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            total = total * 60 + v;
        }
        seconds = total;
        return true;
    }
}
