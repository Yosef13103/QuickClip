using System.Text.Json;
using QuickClip.Library;
using QuickClip.Media;

namespace QuickClip.Tests;

public sealed class ShareRetentionTests
{
    [Fact]
    public void Expiration_uses_full_day_since_last_use()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(ClipSharing.RetentionExpired(now.AddHours(-23), now));
        Assert.False(ClipSharing.RetentionExpired(now.AddDays(1), now));
        Assert.True(ClipSharing.RetentionExpired(now.AddHours(-24), now));
    }

    [Fact]
    public void Retention_accepts_only_exact_generated_child_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), "fixture-shares");
        string id = Guid.NewGuid().ToString("N");
        string owned = Path.Combine(root, "share-" + id, "video.mp4");
        Assert.True(ClipSharing.OwnedSharePath(root, owned, id));
        Assert.False(ClipSharing.OwnedSharePath(root, Path.Combine(root, "video.mp4"), id));
        Assert.False(ClipSharing.OwnedSharePath(root, Path.Combine(root, "share-" + id, "other", "video.mp4"), id));
        Assert.False(ClipSharing.OwnedSharePath(root, owned, Guid.NewGuid().ToString("N")));
        Assert.False(ClipSharing.OwnedSharePath(root, Path.ChangeExtension(owned, ".txt"), id));
    }
    [Fact]
    public void Sweep_removes_only_expired_unchanged_unprotected_owned_files()
    {
        var temp = Directory.CreateTempSubdirectory("quickclip-share-retention-");
        try
        {
            string root = Path.Combine(temp.FullName, "shared");
            string journal = Path.Combine(temp.FullName, "receipts");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(journal);
            var now = DateTimeOffset.UtcNow;
            string expired = Create(now.AddDays(-2));
            string recent = Create(now.AddHours(-2));
            string onClipboard = Create(now.AddDays(-2));
            string altered = Create(now.AddDays(-2));
            File.AppendAllText(altered, "changed");
            string untracked = Create(now.AddDays(-2), tracked: false);
            string other = Path.Combine(Path.GetDirectoryName(expired)!, "keep.txt");
            File.WriteAllText(other, "unowned");
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { onClipboard };
            ClipSharing.SweepExpired(root, journal, protectedPaths, () => false, now);
            Assert.True(File.Exists(expired));
            ClipSharing.SweepExpired(root, journal, protectedPaths, () => true, now);
            Assert.False(File.Exists(expired));
            Assert.All(new[] { recent, onClipboard, altered, untracked, other }, path => Assert.True(File.Exists(path)));

            string Create(DateTimeOffset usedAt, bool tracked = true)
            {
                string id = Guid.NewGuid().ToString("N");
                string dir = Path.Combine(root, "share-" + id);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "fixture.mp4");
                File.WriteAllText(path, "generated fixture");
                if (tracked)
                {
                    var stamp = MediaSource.Capture(path).Stamp;
                    Assert.NotNull(stamp.Identity);
                    File.WriteAllText(Path.Combine(journal, id + ".json"),
                        JsonSerializer.Serialize(new { Path = path, Stamp = stamp, UsedAt = usedAt }));
                }
                return path;
            }
        }
        finally { temp.Delete(recursive: true); }
    }
}
