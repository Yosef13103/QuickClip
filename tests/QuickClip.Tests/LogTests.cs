using System.Diagnostics;
using Xunit.Abstractions;

namespace QuickClip.Tests;

[Collection("media")]
public sealed class LogTests(ITestOutputHelper output)
{
    [Fact]
    public void Disabled_startup_does_not_evaluate_legacy_payload_or_collect_events()
    {
        using var buffer = new DiagnosticBuffer();
        DiagnosticCursor cursor = default;
        Assert.False(buffer.Enabled);
        Assert.False(AppSettings.Parse("{}").RecordingDiagnostics);
        Log.Write(ThrowIfEvaluated());
        buffer.Publish(DiagnosticCategory.General, DiagnosticSeverity.Information, "Ignored while disabled");
        Assert.Null(buffer.Read(ref cursor));
    }

    [Fact]
    public void Disabling_clears_history_and_disposal_prevents_restart()
    {
        using var buffer = new DiagnosticBuffer();
        DiagnosticCursor cursor = default;
        buffer.SetEnabled(true);
        buffer.Publish(DiagnosticCategory.General, DiagnosticSeverity.Information, "First session");
        buffer.SetEnabled(true);
        Assert.Single(buffer.Read(ref cursor)!.Entries);
        Assert.Null(buffer.Read(ref cursor));

        buffer.SetEnabled(false);
        Assert.False(buffer.Enabled);
        Assert.Null(buffer.Read(ref cursor));
        buffer.SetEnabled(true);
        Assert.Empty(buffer.Read(ref cursor)!.Entries);
        buffer.Dispose();
        buffer.SetEnabled(true);
        buffer.Publish(DiagnosticCategory.General, DiagnosticSeverity.Information, "After shutdown");
        Assert.False(buffer.Enabled);
        Assert.Null(buffer.Read(ref cursor));
    }

    [Fact]
    public void Overflow_retains_the_newest_events_in_order()
    {
        using var buffer = new DiagnosticBuffer(3);
        buffer.SetEnabled(true);
        for (int sequence = 0; sequence < 8; sequence++)
            buffer.Publish(DiagnosticCategory.General, DiagnosticSeverity.Information, "Test event", sequence);
        DiagnosticCursor cursor = default;
        var snapshot = buffer.Read(ref cursor)!;
        Assert.Equal(5, snapshot.Dropped);
        Assert.Equal(new int?[] { 5, 6, 7 }, snapshot.Entries.Select(entry => entry.Code));
        Assert.All(snapshot.Entries, entry => Assert.Equal(TimeSpan.Zero, entry.Utc.Offset));
    }

    [Fact]
    public void Concurrent_producers_and_reader_keep_a_bounded_accounted_history()
    {
        const int events = 20_000;
        using var buffer = new DiagnosticBuffer(128);
        buffer.SetEnabled(true);
        Parallel.Invoke(
            () => Parallel.For(0, events, sequence =>
                buffer.Publish(DiagnosticCategory.Audio, DiagnosticSeverity.Information, "Test event", sequence, realtime: true)),
            () =>
            {
                DiagnosticCursor reader = default;
                for (int i = 0; i < 1000; i++)
                    if (buffer.Read(ref reader) is { } batch) Assert.InRange(batch.Entries.Length, 0, 128);
            });
        DiagnosticCursor cursor = default;
        var final = buffer.Read(ref cursor)!;
        Assert.Equal(events, final.Entries.Length + final.Dropped);
        Assert.Equal(final.Entries.Length, final.Entries.Select(entry => entry.Code).Distinct().Count());
    }

    [Fact]
    public void Concurrent_shutdown_cannot_leave_a_live_buffer()
    {
        using var buffer = new DiagnosticBuffer(16);
        buffer.SetEnabled(true);
        Parallel.Invoke(
            () => Parallel.For(0, 1000, _ => buffer.Publish(DiagnosticCategory.General, DiagnosticSeverity.Error, "Test error")),
            buffer.Dispose,
            () => Parallel.For(0, 50, index => buffer.SetEnabled(index % 2 == 0)));
        DiagnosticCursor cursor = default;
        Assert.False(buffer.Enabled);
        Assert.Null(buffer.Read(ref cursor));
    }

    [Fact]
    public void Opt_in_filters_and_explicit_export_exclude_sensitive_payloads()
    {
        const string secret = @"javaw.exe --accessToken test-only-token --username private-user C:\private\secret.mp4 endpoint-id installed-game";
        string path = Path.Combine(AppProfile.TempDirectory, Guid.NewGuid() + ".txt");
        Directory.CreateDirectory(AppProfile.TempDirectory);
        Log.SetEnabled(true);
        try
        {
            Log.Event(DiagnosticCategory.Editor, "Synthetic export started");
            Log.Event(DiagnosticCategory.Editor, "Synthetic export warning", DiagnosticSeverity.Warning);
            Log.Failure(DiagnosticCategory.Editor, "Synthetic export failed", new IOException(secret));
            Log.Failure(DiagnosticCategory.Audio, "Synthetic export failure from audio");
            Log.Write("Legacy diagnostic: " + secret);
            DiagnosticCursor cursor = default;
            var snapshot = Log.Read(ref cursor)!;
            var selected = snapshot.Filter(DiagnosticCategory.Editor, errorsOnly: true, "SYNTHETIC EXPORT").ToArray();
            Assert.Equal("Synthetic export failed", Assert.Single(selected).Message);
            Assert.False(File.Exists(path));
            DiagnosticSnapshot.Export(path, selected);
            string exported = File.ReadAllText(path);
            Assert.Contains("[Error] [Editor] Synthetic export failed (0x", exported);
            Assert.DoesNotContain("Synthetic export warning", exported);
            Assert.DoesNotContain("installed-game", exported);
            Assert.DoesNotContain("endpoint-id", exported);
            Assert.DoesNotContain("--accessToken", exported);
            Assert.DoesNotContain("test-only-token", exported);
            Assert.DoesNotContain("private-user", exported);
            Assert.DoesNotContain("secret", exported);
            Assert.Throws<IOException>(() => DiagnosticSnapshot.Export(path, selected));
            Assert.Equal(exported, File.ReadAllText(path));
        }
        finally
        {
            Log.SetEnabled(false);
            File.Delete(path);
        }
    }

    [Fact]
    public void Essential_exception_is_recorded_without_verbose_logging()
    {
        Assert.False(Log.Enabled);
        Log.Essential(DiagnosticCategory.General, "Synthetic fatal error", new IOException("private exception text"), crash: true);
        using var stream = new FileStream(Log.EssentialFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string text = reader.ReadToEnd();
        Assert.Contains("[Error] [General] Synthetic fatal error (0x", text);
        Assert.DoesNotContain("private exception text", text);
        DiagnosticCursor cursor = default;
        Assert.Null(Log.Read(ref cursor));
    }

    [OptInFact("QUICKCLIP_TEST_DIAGNOSTIC_BENCHMARK")]
    public void Synthetic_producer_and_lifecycle_cost()
    {
        const int iterations = 1_000_000;
        using var process = Process.GetCurrentProcess();
        string legacyPath = Path.Combine(AppProfile.LocalDirectory, "quickclip.log");
        long initialErrorBytes = File.Exists(Log.EssentialFilePath) ? new FileInfo(Log.EssentialFilePath).Length : 0;
        for (int run = 0; run < 6; run++)
        {
            bool enabled = run % 2 == 1;
            long beforeToggle = GC.GetAllocatedBytesForCurrentThread();
            Log.SetEnabled(enabled);
            long toggleBytes = GC.GetAllocatedBytesForCurrentThread() - beforeToggle;
            try
            {
                for (int i = 0; i < 10_000; i++)
                    Log.Event(DiagnosticCategory.General, "Synthetic producer warmup");
                TimeSpan cpu = process.TotalProcessorTime;
                long allocations = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++)
                    Log.Event(DiagnosticCategory.General, "Synthetic producer event", code: i);
                TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocations;
                TimeSpan cpuUsed = process.TotalProcessorTime - cpu;
                DiagnosticCursor cursor = default;
                var snapshot = Log.Read(ref cursor);
                output.WriteLine($"enabled={enabled} events={iterations} wall={elapsed.TotalNanoseconds / iterations:F2} ns/event cpu={cpuUsed.TotalMilliseconds:F2} ms producerAlloc={bytes} B toggleAlloc={toggleBytes} B retained={snapshot?.Entries.Length ?? 0} expired={snapshot?.Dropped ?? 0}");
                Assert.Equal(0, bytes);
                Assert.Equal(enabled ? 2048 : 0, snapshot?.Entries.Length ?? 0);
            }
            finally { Log.SetEnabled(false); }
        }
        Assert.False(File.Exists(legacyPath));
        Assert.Equal(initialErrorBytes, File.Exists(Log.EssentialFilePath) ? new FileInfo(Log.EssentialFilePath).Length : 0);
        output.WriteLine("Automatic diagnostic log IO: 0 bytes, export was not requested.");
    }

    private static string ThrowIfEvaluated() => throw new InvalidOperationException("Legacy logging evaluated its argument.");
}
