using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class ReplayRecoveryJournalTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("qc-replay-recovery-");
    private string Journal => Path.Combine(_root.FullName, "journal");
    private MediaSource FileAt(string name)
    {
        string path = Path.Combine(_root.FullName, name + ".mp4");
        File.WriteAllText(path, name);
        return MediaSource.Capture(path);
    }

    [Fact]
    public void Durable_intent_can_restore_identity_after_partial_batch_and_restart()
    {
        var a = FileAt("a");
        var b = FileAt("b");
        var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        using (var locked = b.OpenVerified())
        {
            var failure = Assert.Throws<ExportException>(() => transaction.Stage(default));
            Assert.False(File.Exists(a.Path), failure.Message);
        }
        b.Verify();
        output.Verify();

        transaction = ReplayRecoveryJournal.Open(Journal, transaction.Id);
        Assert.Equal(ReplayOriginalState.Escrowed, transaction.Inspect(transaction.Items[0]));
        Assert.Equal(ReplayOriginalState.Original, transaction.Inspect(transaction.Items[1]));
        transaction.Restore(default);
        transaction.Restore(default);
        a.Verify(); b.Verify(); output.Verify();
        Assert.All(transaction.Items, item => Assert.False(File.Exists(item.Escrow)));
    }

    [Fact]
    public void Staging_is_idempotent_and_undo_does_not_overwrite_a_new_occupant()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        transaction.Stage(default);
        foreach (var item in transaction.Items)
        {
            Assert.Equal(ClipAvailability.Missing, LibraryFiles.InspectPath(item.Source.Path, false));
            Assert.Equal(ClipAvailability.Available, LibraryFiles.InspectPath(item.Escrow, false));
            Assert.Equal(item.Source.Stamp, MediaSource.Capture(item.Escrow).Stamp);
        }
        transaction.Stage(default);
        File.WriteAllText(a.Path, "new occupant");
        transaction = ReplayRecoveryJournal.Open(Journal, transaction.Id);
        Assert.Equal(ReplayOriginalState.Conflict, transaction.Inspect(transaction.Items[0]));
        Assert.Throws<ExportException>(() => transaction.Restore(default));
        Assert.Equal("new occupant", File.ReadAllText(a.Path));
        (a with { Path = transaction.Items[0].Escrow }).Verify();
        (b with { Path = transaction.Items[1].Escrow }).Verify();
        output.Verify();
        // Fixture-only collision resolution; never part of recovery itself.
        File.Delete(a.Path);
        transaction.Restore(default);
        a.Verify(); b.Verify(); output.Verify();
    }

    [Fact]
    public void Changed_output_or_missing_journal_never_consumes_originals()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        File.AppendAllText(output.Path, "changed");
        Assert.Throws<ExportException>(() => transaction.Stage(default));
        Assert.Throws<ExportException>(() => ReplayRecoveryJournal.Open(Journal, Guid.NewGuid()));
        a.Verify(); b.Verify();
    }

    [Fact]
    public void Cancellation_and_destination_collision_preserve_originals()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        Assert.Throws<OperationCanceledException>(() => transaction.Stage(new CancellationToken(true)));
        File.WriteAllText(transaction.Items[0].Escrow, "occupied");
        Assert.Throws<ExportException>(() => transaction.Stage(default));
        Assert.Equal("occupied", File.ReadAllText(transaction.Items[0].Escrow));
        a.Verify(); b.Verify(); output.Verify();
    }

    [Fact]
    public void Missing_or_changed_durable_intent_blocks_staging()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        string record = Path.Combine(Journal, transaction.Id.ToString("N") + ".json");
        File.Delete(record);
        Assert.Throws<ExportException>(() => transaction.Stage(default));
        a.Verify(); b.Verify(); output.Verify();

        transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        record = Path.Combine(Journal, transaction.Id.ToString("N") + ".json");
        File.WriteAllText(record, "{}");
        Assert.Throws<ExportException>(() => transaction.Stage(default));
        a.Verify(); b.Verify(); output.Verify();
    }

    public void Dispose() => _root.Delete(recursive: true);
}