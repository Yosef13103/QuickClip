using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class LibraryReplayPublicationTests
{
    [Fact]
    public async Task Exact_publication_indexes_only_new_replay_and_repairs_untouched_racing_row()
    {
        var scratch = Directory.CreateTempSubdirectory("qc-replay-publication-");
        try
        {
            string root = scratch.CreateSubdirectory("clips").FullName;
            string sidecars = scratch.CreateSubdirectory("details").FullName;
            string db = Path.Combine(scratch.FullName, "library.sqlite3");
            int probes = 0;
            var index = new LibraryIndex(db, sidecars, _ => { probes++; return new ClipHeader(9, null); });
            string old = Path.Combine(root, "old.mp4");
            File.WriteAllText(old, "old");
            var id = await index.RegisterRootAsync(root);
            await index.ReconcileAsync(id);
            var existing = Assert.Single(await index.ReadGalleryAsync(id, null));
            var details = new ClipDetails(DateTimeOffset.UnixEpoch, "Fixture") { Bookmarks = [new(3, "Saved")] };

            MediaSource Create(string name)
            {
                string path = Path.Combine(root, name + ".mp4");
                File.WriteAllText(path, name);
                return MediaSource.Capture(path);
            }
            void Sidecar(MediaSource source)
            {
                string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Path.ToUpperInvariant())));
                File.WriteAllText(Path.Combine(sidecars, key + ".json"), JsonSerializer.Serialize(new
                {
                    CreatedTicks = source.Stamp.Created, ModifiedTicks = source.Stamp.Modified,
                    Length = source.Stamp.Bytes, Details = details,
                }));
            }
            var fresh = Create("fresh");
            var unrelated = Create("unrelated");
            Sidecar(fresh);
            Assert.True(await index.RegisterPublishedReplayAsync(id, fresh, default));
            var rows = (await index.ReadGalleryAsync(id, null)).ToDictionary(row => row.Path);
            Assert.Equal(2, rows.Count);
            Assert.Equal(existing.Details.IndexId, rows[old].Details.IndexId);
            Assert.Equal("Saved", Assert.Single(rows[fresh.Path].Details.Bookmarks).Label);
            Assert.Equal(2, probes);
            Assert.False(await index.RegisterPublishedReplayAsync(id, unrelated, default));

            var racing = Create("racing");
            await index.ReconcileAsync(id);
            var stale = Assert.Single(await index.ReadGalleryAsync(id, racing.Path));
            Sidecar(racing);
            Assert.True(await index.RegisterPublishedReplayAsync(id, racing, default));
            var repaired = Assert.Single(await index.ReadGalleryAsync(id, racing.Path));
            Assert.Equal(stale.Details.IndexId, repaired.Details.IndexId);
            Assert.Equal(stale.Details.IndexRevision + 1, repaired.Details.IndexRevision);
            Assert.Equal("Saved", Assert.Single(repaired.Details.Bookmarks).Label);
            await Assert.ThrowsAsync<LibraryConflictException>(() => index.SaveGalleryDetailsAsync(
                new(racing.Path, stale.Stamp, stale.Details), stale.Details with { IsFavorite = true }, default));
            Assert.False(await index.RegisterPublishedReplayAsync(id, racing, default));
        }
        finally { scratch.Delete(recursive: true); }
    }
}