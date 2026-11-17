using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Views;

namespace QuickClip.Tests;

public sealed class ClipEditDraftTests : IDisposable
{
    private readonly DirectoryInfo _scratch = Directory.CreateTempSubdirectory("qc-edit-drafts-");
    private string Database => Path.Combine(_scratch.FullName, "clips.sqlite3");

    private static readonly ClipEditDraft Edited = new(1.25, 4.75, 2.7, true,
        [new(1, true, 1.32, "Game"), new(2, false, 0, "Chat"), new(3, true, 0.48, "Mic")],
        new(true, 5, 10, 320, 180, 1));

    [Fact]
    public async Task Restart_restores_multiple_tracks_and_gallery_duration_then_reset_removes_draft()
    {
        var (path, stamp) = Media("a.mp4");
        await new LibraryIndex(Database).SaveEditDraftAsync(path, stamp, null, Edited);

        // A new index instance opens independent SQLite connections with no in-memory editor state.
        var reopened = new LibraryIndex(Database);
        var stored = Assert.IsType<StoredClipDraft>(await reopened.ReadEditDraftAsync(path, stamp));
        var draft = Assert.IsType<ClipEditDraft>(stored.State);
        Assert.Equal((Edited.Start, Edited.End, Edited.Position, Edited.Loop), (draft.Start, draft.End, draft.Position, draft.Loop));
        Assert.Equal(Edited.Tracks, draft.Tracks);
        Assert.Equal(Edited.Crop, draft.Crop);

        var clip = Card(path);
        var ranges = await reopened.ReadDraftRangesAsync([clip], CancellationToken.None);
        clip.ApplyEditRange(ranges[path]);
        Assert.Equal(3.5, clip.SelectedDuration);
        Assert.Equal(6, clip.Duration);
        Assert.Equal(Edited.Start, ranges[path].Start);
        Assert.Equal(Edited.End, ranges[path].End);
        Assert.Equal("synthetic media identity fixture", File.ReadAllText(path));

        await reopened.SaveEditDraftAsync(path, stamp, stored.Payload, null);
        Assert.Null(await new LibraryIndex(Database).ReadEditDraftAsync(path, stamp));
        Assert.Empty(await reopened.ReadDraftRangesAsync([clip], CancellationToken.None));
    }

    [Fact]
    public async Task Replacement_and_export_start_clean_even_with_matching_file_facts_and_undo_restores_draft()
    {
        var (path, stamp) = Media("a.mp4");
        var index = new LibraryIndex(Database);
        await index.SaveEditDraftAsync(path, stamp, null, Edited);
        string recycled = Path.Combine(_scratch.FullName, "simulated-bin.mp4");
        File.Move(path, recycled);
        await Assert.ThrowsAnyAsync<IOException>(() => index.ReadEditDraftAsync(path, stamp));

        var (export, _) = Media("export.mp4");
        File.SetCreationTimeUtc(export, new(stamp.Created, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(export, new(stamp.Modified, DateTimeKind.Utc));
        var exportStamp = Stamp(export);
        Assert.NotEqual(stamp.Identity, exportStamp.Identity);
        Assert.Null(await index.ReadEditDraftAsync(export, exportStamp));
        File.Move(export, path);
        Assert.Empty(await index.ReadDraftRangesAsync([Card(path)], CancellationToken.None));
        await Assert.ThrowsAnyAsync<IOException>(() => index.SaveEditDraftAsync(path, stamp, Edited.Serialize(), null));
        Assert.Null(await index.ReadEditDraftAsync(path, exportStamp));

        File.Move(path, export);
        File.Move(recycled, path);
        Assert.Equal(stamp, Stamp(path));
        Assert.Equal(Edited.Serialize(), (await index.ReadEditDraftAsync(path, stamp))?.Payload);
    }

    [Fact]
    public async Task Moves_keep_their_draft_but_in_place_changes_and_other_clips_use_their_own_state()
    {
        var (first, firstStamp) = Media("first.mp4");
        var (second, secondStamp) = Media("second.mp4");
        var next = Edited with { Start = 0, Position = 4 };
        var index = new LibraryIndex(Database);
        await Task.WhenAll(index.SaveEditDraftAsync(first, firstStamp, null, Edited),
            index.SaveEditDraftAsync(second, secondStamp, null, next));

        string renamed = Path.Combine(_scratch.FullName, "renamed.mp4");
        File.Move(first, renamed);
        var ranges = await index.ReadDraftRangesAsync([Card(renamed), Card(second)], CancellationToken.None);
        Assert.Equal(Edited.Start, ranges[renamed].Start);
        Assert.Equal(next.Start, ranges[second].Start);

        File.AppendAllText(renamed, " changed");
        Assert.Null(await index.ReadEditDraftAsync(renamed, Stamp(renamed)));
        Assert.False((await index.ReadDraftRangesAsync([Card(renamed)], CancellationToken.None)).ContainsKey(renamed));
    }

    [Fact]
    public async Task Failed_saves_leave_the_previous_draft_and_can_be_retried()
    {
        var (path, stamp) = Media("a.mp4");
        var index = new LibraryIndex(Database);
        await index.SaveEditDraftAsync(path, stamp, null, Edited);
        var next = Edited with { Position = 3 };
        await Assert.ThrowsAsync<LibraryConflictException>(() => index.SaveEditDraftAsync(path, stamp, null, next));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            index.SaveEditDraftAsync(path, stamp, Edited.Serialize(), next with { End = 0 }));

        var previous = Assert.IsType<StoredClipDraft>(await index.ReadEditDraftAsync(path, stamp));
        Assert.Equal(Edited.Serialize(), previous.Payload);
        await index.SaveEditDraftAsync(path, stamp, previous.Payload, next);
        Assert.Equal(next.Serialize(), (await index.ReadEditDraftAsync(path, stamp))?.Payload);
    }

    private (string Path, FileStamp Stamp) Media(string name)
    {
        string path = Path.Combine(_scratch.FullName, name);
        File.WriteAllText(path, "synthetic media identity fixture");
        return (path, Stamp(path));
    }

    private static FileStamp Stamp(string path)
    {
        using var handle = LibraryFiles.OpenMedia(path);
        return LibraryFiles.ReadStamp(handle);
    }

    private static ClipItem Card(string path) =>
        new(new FileInfo(path), new ClipDetails(DateTimeOffset.UtcNow, "Fixture"), new(6, null));

    public void Dispose() => _scratch.Delete(recursive: true);
}
