using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class BackgroundActivityTests
{
    [Fact]
    public void Concurrent_leases_report_real_progress_and_disappear_without_history()
    {
        if (!AppProfile.IsPersonal) return;
        using var a = BackgroundActivity.Begin("Fixture indexing");
        using var b = BackgroundActivity.Begin("Fixture export");
        var rows = BackgroundActivity.Snapshot(0);
        var first = Assert.Single(rows.Where(row => row.Label == "Fixture indexing"));
        Assert.Null(first.Fraction);
        Assert.DoesNotContain(BackgroundActivity.Snapshot(long.MaxValue), row => row.Id == first.Id);
        b.Report("Fixture export", .42);
        Assert.Equal(.42, Assert.Single(BackgroundActivity.Snapshot(0).Where(row => row.Label == "Fixture export")).Fraction);
        a.Dispose();
        a.Report("Fixture indexing stale", 1);
        Assert.DoesNotContain(BackgroundActivity.Snapshot(0), row => row.Id == first.Id);
        b.Report("Fixture export", double.NaN);
        Assert.Null(Assert.Single(BackgroundActivity.Snapshot(0).Where(row => row.Label == "Fixture export")).Fraction);
        b.Dispose();
        Assert.DoesNotContain(BackgroundActivity.Snapshot(0), row => row.Label.StartsWith("Fixture "));
    }
}
