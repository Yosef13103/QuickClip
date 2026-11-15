using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using QuickClip.Recording;

namespace QuickClip;

internal static class Log
{
    private static readonly DiagnosticBuffer Buffer = new();

    public static bool Enabled => Buffer.Enabled;
    internal static string EssentialFilePath => Path.Combine(AppProfile.LocalDirectory, "quickclip-errors.log");
    public static DiagnosticSnapshot? Read(ref DiagnosticCursor cursor) => Buffer.Read(ref cursor);

    public static void SetEnabled(bool enabled)
    {
        Buffer.SetEnabled(enabled);
        CaptureEngine.UpdateDiagnosticLogging();
    }

    public static void Shutdown()
    {
        Buffer.Dispose();
        CaptureEngine.UpdateDiagnosticLogging();
    }

    // Only source-controlled summaries and numeric status codes belong in diagnostics.
    public static void Event(DiagnosticCategory category, [ConstantExpected] string message,
        DiagnosticSeverity severity = DiagnosticSeverity.Information, int? code = null) =>
        Buffer.Publish(category, severity, message, code);

    public static void Failure(DiagnosticCategory category, [ConstantExpected] string message, Exception? exception = null) =>
        Event(category, message, DiagnosticSeverity.Error, exception?.HResult);

    public static void Essential(DiagnosticCategory category, [ConstantExpected] string message,
        Exception? exception = null, bool crash = false)
    {
        Failure(category, message, exception);
        Failures.Write(category, message, exception, crash);
    }

    public static void Native(DiagnosticCategory category, DiagnosticSeverity severity, string message, int? code) =>
        Buffer.Publish(category, severity, message, code, realtime: true);

    // The remaining callers belong to other lanes. Compile out the calls and all argument evaluation
    // until those owners migrate to categorized events with safe payloads.
    [Conditional("QUICKCLIP_REMOVED_LEGACY_LOG")]
    public static void Write(string _) { }

    // Initialized only on an essential failure. The bounded file is independent of verbose logging.
    private static class Failures
    {
        private const long FileLimit = 128 * 1024;
        private static readonly object Gate = new();
        private static readonly long[] NextWriteAt = new long[Enum.GetValues<DiagnosticCategory>().Length];

        internal static void Write(DiagnosticCategory category, string message, Exception? exception, bool crash)
        {
            lock (Gate)
            {
                long now = Environment.TickCount64;
                if (!crash && now < NextWriteAt[(int)category]) return;
                NextWriteAt[(int)category] = now + 2_000;
                try
                {
                    string path = EssentialFilePath;
                    Directory.CreateDirectory(AppProfile.LocalDirectory);
                    if (new FileInfo(path) is { Exists: true, Length: >= FileLimit })
                        File.Move(path, path + ".old", overwrite: true);
                    using var writer = new StreamWriter(path, append: true);
                    var entry = new DiagnosticEntry(DateTimeOffset.UtcNow, category, DiagnosticSeverity.Error, message, exception?.HResult);
                    writer.WriteLine(entry.ToLine());
                    if (crash && exception != null)
                    {
                        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames()
                            .Select(frame => frame.GetMethod())
                            .Where(method => method?.DeclaringType?.Assembly == typeof(Log).Assembly)
                            .Take(6);
                        foreach (var method in frames)
                            writer.WriteLine($"    {method!.DeclaringType!.Name}.{method.Name}");
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
