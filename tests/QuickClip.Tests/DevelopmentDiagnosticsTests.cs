namespace QuickClip.Tests;

public sealed class DevelopmentDiagnosticsTests
{
    [Fact]
    public async Task Incremental_flush_rotates_keeps_two_bounded_files_and_restarts()
    {
        var scratch = Directory.CreateTempSubdirectory("qc-development-logs-");
        try
        {
            using var buffer = new DiagnosticBuffer();
            buffer.SetEnabled(true);
            var writer = new DevelopmentDiagnostics(buffer, scratch.FullName);
            string path = Path.Combine(scratch.FullName, "development.log");
            string payload = new('x', 750);
            for (int i = 0; i < 2000; i++)
                buffer.Publish(DiagnosticCategory.Editor, DiagnosticSeverity.Information, $"fixture {i} {payload}");
            await writer.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, scratch.GetFiles().Length);
            Assert.All(scratch.GetFiles(), file => Assert.InRange(file.Length, 1, DevelopmentDiagnostics.FileLimit));
            string firstSession = (await File.ReadAllLinesAsync(path))[0].Split(' ')[0];

            var restarted = new DevelopmentDiagnostics(buffer, scratch.FullName);
            buffer.SetEnabled(false);
            buffer.SetEnabled(true);
            buffer.Publish(DiagnosticCategory.Editor, DiagnosticSeverity.Information, "second session fixture");
            await restarted.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string[] log = await File.ReadAllLinesAsync(path);
            string last = Assert.Single(log, line => line.EndsWith("second session fixture", StringComparison.Ordinal));
            Assert.NotEqual(firstSession, last.Split(' ')[0]);
            Assert.All(scratch.GetFiles(), file => Assert.InRange(file.Length, 1, DevelopmentDiagnostics.FileLimit));
        }
        finally { scratch.Delete(recursive: true); }
    }

    [Fact]
    public async Task File_contention_leaves_publisher_responsive_and_writer_recovers()
    {
        var scratch = Directory.CreateTempSubdirectory("qc-contended-logs-");
        try
        {
            using var buffer = new DiagnosticBuffer(capacity: 128);
            buffer.SetEnabled(true);
            string path = Path.Combine(scratch.FullName, "development.log");
            DevelopmentDiagnostics writer;
            using (File.Create(path))
            {
                writer = new(buffer, scratch.FullName);
                for (int i = 0; i < 1000; i++)
                    buffer.Publish(DiagnosticCategory.Editor, DiagnosticSeverity.Information, "during storage contention");
                await Task.Delay(600);
            }
            buffer.Publish(DiagnosticCategory.Editor, DiagnosticSeverity.Information, "storage recovered");
            await writer.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("storage recovered", await File.ReadAllTextAsync(path));
            DiagnosticCursor cursor = default;
            Assert.Equal(128, buffer.Read(ref cursor)!.Entries.Length);
        }
        finally { scratch.Delete(recursive: true); }
    }
}
