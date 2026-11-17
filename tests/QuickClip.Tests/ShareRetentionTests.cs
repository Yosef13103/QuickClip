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
}
