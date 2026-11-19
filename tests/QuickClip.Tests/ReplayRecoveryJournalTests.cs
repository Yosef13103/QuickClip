using QuickClip.Library;
using QuickClip.Media;
using QuickClip.Shell;

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

    [Fact]
    public void Hardlinked_original_is_not_mutated_or_hidden()
    {
        var a = FileAt("a");
        var b = FileAt("b");
        var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        string link = Path.Combine(_root.FullName, "project-alias.mp4");
        Assert.True(CreateHardLinkW(link, a.Path, IntPtr.Zero),
            $"Could not create the disposable hardlink (Windows error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");
        Assert.Equal(a.Stamp.Identity, MediaSource.Capture(link).Stamp.Identity);
        Assert.Throws<ExportException>(() => transaction.Stage(default));
        a.Verify(); b.Verify(); output.Verify();
        Assert.Equal(a.Stamp.Identity, MediaSource.Capture(link).Stamp.Identity);
        Assert.All(transaction.Items, item => Assert.False(File.Exists(item.Escrow)));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    [Fact]
    public void Partial_recycle_can_be_recovered_after_restart_without_receipt_checkpoint()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        transaction.Stage(default);
        var bin = _root.CreateSubdirectory("fake-bin");
        var receipts = new Dictionary<string, RecycledFile>();
        bool Recycle(string path)
        {
            if (receipts.Count > 0) return false;
            string data = Path.Combine(bin.FullName, "$R" + Guid.NewGuid().ToString("N"));
            File.Move(path, data);
            receipts[path] = new(data, data + ".info", path, DateTime.UtcNow);
            return false; // interruption/failure after the shell moved the file
        }
        RecycledFile? Find(string path) => receipts.GetValueOrDefault(path);
        Assert.Throws<ExportException>(() => transaction.RecycleEscrow(default, Recycle, Find));
        transaction = ReplayRecoveryJournal.Open(Journal, transaction.Id);
        Assert.Equal(ReplayOriginalState.Recycled, transaction.Inspect(transaction.Items[0], Find));
        Assert.Equal(ReplayOriginalState.Escrowed, transaction.Inspect(transaction.Items[1], Find));
        transaction.Restore(default, Find);
        transaction.Restore(default, Find);
        a.Verify(); b.Verify(); output.Verify();
    }

    [Fact]
    public void Recycle_recovery_rejects_wrong_receipt_and_preserves_new_occupant()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        transaction.Stage(default);
        var item = transaction.Items[0];
        string data = Path.Combine(_root.CreateSubdirectory("fake-bin").FullName, "$Rfixture");
        File.Move(item.Escrow, data);
        RecycledFile? Wrong(string path) => new(output.Path, data + ".info", path, DateTime.UtcNow);
        Assert.Equal(ReplayOriginalState.Conflict, transaction.Inspect(item, Wrong));
        Assert.Throws<ExportException>(() => transaction.Restore(default, Wrong));
        RecycledFile? Correct(string path) => path == item.Escrow ? new(data, data + ".info", path, DateTime.UtcNow) : null;
        File.WriteAllText(a.Path, "new occupant");
        Assert.Throws<ExportException>(() => transaction.Restore(default, Correct));
        Assert.Equal("new occupant", File.ReadAllText(a.Path));
        (a with { Path = data }).Verify();
        File.Delete(a.Path); // fixture collision resolution only
        transaction.Restore(default, Correct);
        a.Verify(); b.Verify(); output.Verify();
    }

    [OptInFact("QUICKCLIP_TEST_RECYCLE_BIN")]
    public async Task Real_shell_recycles_and_restores_exact_originals_after_reopening_journal()
    {
        var a = FileAt("a"); var b = FileAt("b"); var output = FileAt("combined");
        var transaction = ReplayRecoveryJournal.Prepare(Journal, output, [a, b]);
        transaction.Stage(default);
        DateTime since = DateTime.UtcNow.AddSeconds(-2);
        RecycledFile? Find(string path) => RecycleBin.FindLatest(path, since);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                transaction.RecycleEscrow(default, RecycleBin.TrySend, Find);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        try
        {
            thread.Start();
            await completion.Task;
            transaction = ReplayRecoveryJournal.Open(Journal, transaction.Id);
            Assert.All(transaction.Items, item => Assert.Equal(ReplayOriginalState.Recycled, transaction.Inspect(item, Find)));
        }
        finally
        {
            // Recover only these uniquely named test originals, even if an assertion fails.
            transaction.Restore(default, Find);
        }
        a.Verify(); b.Verify(); output.Verify();
    }

    public void Dispose() => _root.Delete(recursive: true);
}