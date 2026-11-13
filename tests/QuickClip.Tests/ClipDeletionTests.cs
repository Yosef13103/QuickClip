using QuickClip.Gallery;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class ClipDeletionTests
{
    [Fact]
    public async Task Cancelled_batch_does_not_unload_or_recycle_and_defaults_to_confirmation()
    {
        Assert.True(AppSettings.Parse("{}").ConfirmClipDeletion);
        Assert.False(AppSettings.Parse("{\"ConfirmClipDeletion\":false}").ConfirmClipDeletion);
        var deletion = new ClipDeletion(_ => throw new Exception("must not recycle"), (_, _) => null, (_, _) => { });
        var result = await deletion.DeleteAsync([Path.GetFullPath("clip.mp4")], _ => false, () => throw new Exception("must not unload"));
        Assert.True(result.Cancelled);
        Assert.False(deletion.Busy);
    }

    [Fact]
    public async Task Bulk_delete_confirms_once_deduplicates_and_rejects_reentrant_input()
    {
        int prompts = 0, recycled = 0;
        var deletion = new ClipDeletion(_ => { recycled++; return true; }, (_, _) => null, (_, _) => { });
        var directory = Directory.CreateTempSubdirectory("qc-batch-");
        try
        {
            string first = Path.Combine(directory.FullName, "a.mp4"), second = Path.Combine(directory.FullName, "b.mp4");
            File.WriteAllText(first, "fixture");
            File.WriteAllText(second, "fixture");
            var result = await deletion.DeleteAsync([first, second, first], paths =>
            {
                prompts++;
                Assert.Equal(2, paths.Count);
                Assert.True(deletion.DeleteAsync([first], _ => throw new Exception("reentrant confirmation")).GetAwaiter().GetResult().Cancelled);
                return true;
            });
            Assert.Equal((2, 0, 1, 2), (result.Deleted, result.Failed, prompts, recycled));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Undo_preserves_replacements_and_can_restore_later()
    {
        string dir = Directory.CreateTempSubdirectory("qc-delete-").FullName;
        try
        {
            string original = Path.Combine(dir, "clip.mp4"), bin = Path.Combine(dir, "recycled.mp4");
            File.WriteAllText(original, "original");
            var receipt = new RecycledFile(bin, bin + ".note", original, DateTime.UtcNow);
            var deletion = new ClipDeletion(path => { File.Move(path, bin); return true; }, (_, _) => receipt,
                (entry, destination) => File.Move(entry.DataPath, destination));
            Assert.Equal(1, (await deletion.DeleteAsync([original], _ => true)).Deleted);
            File.WriteAllText(original, "replacement");
            Assert.Equal(1, (await deletion.UndoAsync()).Failed);
            Assert.Equal("replacement", File.ReadAllText(original));
            Assert.True(deletion.CanUndo);
            File.Delete(original);
            Assert.Equal(1, (await deletion.UndoAsync()).Deleted);
            Assert.Equal("original", File.ReadAllText(original));
            Assert.False(deletion.CanUndo);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Partial_batch_uses_an_sta_worker_and_retains_recovery_for_successes()
    {
        var directory = Directory.CreateTempSubdirectory("qc-partial-recycle-");
        try
        {
            string clip = Path.Combine(directory.FullName, "clip.mp4");
            string bin = Path.Combine(directory.FullName, "recycled.mp4");
            string missing = Path.Combine(directory.FullName, "missing.mp4");
            var archive = directory.CreateSubdirectory("SteelSeries Moments");
            string protectedClip = Path.Combine(archive.FullName, "protected.mp4");
            File.WriteAllText(clip, "fixture");
            File.WriteAllText(protectedClip, "archive");
            var deletion = new ClipDeletion(path =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                Assert.Equal(clip, path);
                File.Move(path, bin);
                return true;
            }, (_, _) => new(bin, bin + ".note", clip, DateTime.UtcNow),
                (receipt, destination) => File.Move(receipt.DataPath, destination));

            var result = await deletion.DeleteAsync([clip, missing, protectedClip], paths => paths.Count == 3);
            Assert.Equal((1, 2), (result.Deleted, result.Failed));
            Assert.Equal("archive", File.ReadAllText(protectedClip));
            Assert.Equal(1, (await deletion.UndoAsync()).Deleted);
            Assert.Equal("fixture", File.ReadAllText(clip));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(0x80u, true)]
    [InlineData(0x82u, true)]
    public void Shell_vetoes_permanent_deletion(uint flags, bool allowed) => Assert.Equal(allowed, ShellRecycle.IsRecycling(flags));

}
