using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class ReplayReplacementPolicyTests
{
    [Fact]
    public async Task Policy_rechecks_favorites_drafts_and_metadata_and_restoration_keeps_index_identity()
    {
        var scratch = Directory.CreateTempSubdirectory("qc-replay-policy-");
        try
        {
            string root = scratch.CreateSubdirectory("clips").FullName;
            string a = Path.Combine(root, "a.mp4"), b = Path.Combine(root, "b.mp4");
            string output = Path.Combine(root, "combined.mp4");
            foreach (var path in new[] { a, b, output }) File.WriteAllText(path, path);
            string db = Path.Combine(scratch.FullName, "library.sqlite3");
            string directory = scratch.CreateSubdirectory("recovery").FullName;
            var index = new LibraryIndex(db, null, _ => new ClipHeader(10, null));
            var id = await index.RegisterRootAsync(root);
            await index.ReconcileAsync(id);
            var rows = (await index.ReadGalleryAsync(id, null)).ToDictionary(row => row.Path);
            var sources = new[] { MediaSource.Capture(a), MediaSource.Capture(b) };
            var journal = ReplayRecoveryJournal.Prepare(directory, MediaSource.Capture(output), sources);
            var policy = new ReplayReplacementPolicy(new(db), path => LibraryFiles.Within(path, root), _ => false);
            ClipDetails[] original = [rows[a].Details, rows[b].Details];
            await index.SaveGalleryDetailsAsync(new(a, rows[a].Stamp, rows[a].Details),
                rows[a].Details with { IsFavorite = true, Tags = ["Keep"], Bookmarks = [new(2, "Marker")] }, default);
            Assert.Throws<ExportException>(() => policy.Stage(journal, original, false, default));
            rows = (await index.ReadGalleryAsync(id, null)).ToDictionary(row => row.Path);
            ClipDetails[] fresh = [rows[a].Details, rows[b].Details];
            Assert.Throws<ExportException>(() => policy.Stage(journal, fresh, true, default));
            var draft = new ClipEditDraft(1, 9, 2, false, [], null);
            await index.SaveEditDraftAsync(b, sources[1].Stamp, null, draft);
            Assert.Throws<ExportException>(() => policy.Stage(journal, fresh, false, default));
            foreach (var source in sources) source.Verify();
            var saved = await index.ReadEditDraftAsync(b, sources[1].Stamp);
            Assert.NotNull(saved);
            await index.SaveEditDraftAsync(b, sources[1].Stamp, saved.Payload, null);
            var blocked = new ReplayReplacementPolicy(new(db), _ => false, _ => false);
            Assert.Throws<ExportException>(() => blocked.Stage(journal, fresh, false, default));
            var recovery = new ReplayReplacementPolicy(new(db), _ => true, _ => true);
            Assert.Throws<ExportException>(() => recovery.Stage(journal, fresh, false, default));
            policy.Stage(journal, fresh, false, default);
            Assert.All(journal.Items, item => Assert.Equal(ReplayOriginalState.Escrowed, journal.Inspect(item)));
            await index.ReconcileAsync(id);
            Assert.Single(await index.ReadGalleryAsync(id, null));
            journal.Restore(default);
            await index.ReconcileAsync(id);
            rows = (await index.ReadGalleryAsync(id, null)).ToDictionary(row => row.Path);
            Assert.Equal(3, rows.Count);
            Assert.Equal(fresh[0].IndexId, rows[a].Details.IndexId);
            Assert.Equal(fresh[1].IndexId, rows[b].Details.IndexId);
            Assert.True(rows[a].Details.IsFavorite);
            Assert.Equal("Keep", Assert.Single(rows[a].Details.Tags));
            Assert.Equal(fresh[0].Bookmarks[0].Id, Assert.Single(rows[a].Details.Bookmarks).Id);
        }
        finally { scratch.Delete(recursive: true); }
    }
}