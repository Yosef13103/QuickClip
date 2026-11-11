using QuickClip.Gallery;
using QuickClip.Shell;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class EditorSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("qc-editor-source-").FullName;

    [Fact]
    public async Task Gallery_recycle_and_undo_change_the_source_only_for_the_confirmed_path()
    {
        string source = Fixture("clip.mp4"), other = Fixture("other.mp4"), bin = Path.Combine(_directory, "bin.mp4");
        Assert.Equal(EditorSourceState.Ready, EditorSource.Check(source, null, out var stamp));
        var deletion = new ClipDeletion(path => { File.Move(path, bin); return true; },
            (path, _) => new RecycledFile(bin, bin + ".note", path, DateTime.UtcNow),
            (item, destination) => File.Move(item.DataPath, destination));
        int releases = 0;
        var states = new List<EditorSourceState>();
        deletion.Releasing += paths => { if (EditorSource.Matches(source, paths)) releases++; };
        deletion.Changed += () => states.Add(EditorSource.Check(source, stamp, out _));
        Assert.True((await deletion.DeleteAsync([source], _ => false)).Cancelled);
        Assert.Equal(1, (await deletion.DeleteAsync([other], _ => true)).Deleted);
        Assert.Equal(1, deletion.Undo().Deleted);
        Assert.Equal(0, releases);
        Assert.True(EditorSource.Matches(source, [source.ToUpperInvariant()]));
        Assert.Equal(1, (await deletion.DeleteAsync([source], _ => true)).Deleted);
        Assert.Equal(1, deletion.Undo().Deleted);
        Assert.Equal(1, releases);
        Assert.Equal(new[] { EditorSourceState.Ready, EditorSourceState.Ready, EditorSourceState.Ready,
            EditorSourceState.Missing, EditorSourceState.Ready }, states);
    }

    [Fact]
    public async Task Failed_recycle_leaves_the_original_readable_after_release()
    {
        string source = Fixture("clip.mp4");
        EditorSource.Check(source, null, out var stamp);
        var deletion = new ClipDeletion(_ => false, (_, _) => null, (_, _) => { });
        Assert.Equal(1, (await deletion.DeleteAsync([source], _ => true)).Failed);
        Assert.Equal(EditorSourceState.Ready, EditorSource.Check(source, stamp, out _));
    }

    [Fact]
    public void Export_checks_reject_locked_missing_and_replaced_source_without_touching_existing_output()
    {
        string source = Fixture("clip.mp4"), existing = Fixture("existing.mp4");
        Assert.Equal(EditorSourceState.Ready, EditorSource.Check(source, null, out var stamp));
        using (File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(EditorSourceState.Unreadable, EditorSource.Check(source, stamp, out _));
        string moved = Path.Combine(_directory, "moved.mp4");
        File.Move(source, moved);
        Assert.Equal(EditorSourceState.Missing, EditorSource.Check(source, stamp, out _));
        Fixture("clip.mp4");
        Assert.Equal(EditorSourceState.Replaced, EditorSource.Check(source, stamp, out _));
        Assert.Equal("existing.mp4", File.ReadAllText(existing));
    }

    private string Fixture(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, name);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
