using System.Text;
using QuickClip.Shell;

namespace QuickClip.Tests;

public sealed class RecycleBinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quickclip-bin-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string TempFile(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Reads_where_a_recycled_file_came_from_in_both_note_layouts()
    {
        const string original = @"C:\Users\me\Videos\QuickClip\Deadlock\Deadlock 2026-10-17 00-48-48.mp4";
        var deleted = new DateTime(2026, 10, 16, 23, 54, 21, DateTimeKind.Utc);

        // Windows 10 and 11: version 2, then the path's length and the path.
        var v2 = new List<byte>();
        v2.AddRange(BitConverter.GetBytes(2L));
        v2.AddRange(BitConverter.GetBytes(792_002_817L));
        v2.AddRange(BitConverter.GetBytes(deleted.ToFileTimeUtc()));
        v2.AddRange(BitConverter.GetBytes(original.Length + 1));
        v2.AddRange(Encoding.Unicode.GetBytes(original + "\0"));
        Assert.Equal((original, deleted), RecycleBin.ReadInfo(TempFile("$I2ABCDE.mp4", [.. v2])));

        // Windows Vista to 8.1: version 1 and a fixed 260-character path.
        var v1 = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(v1, 0);
        BitConverter.GetBytes(deleted.ToFileTimeUtc()).CopyTo(v1, 16);
        Encoding.Unicode.GetBytes(original).CopyTo(v1, 24);
        Assert.Equal((original, deleted), RecycleBin.ReadInfo(TempFile("$I1ABCDE.mp4", v1)));

        Assert.Null(RecycleBin.ReadInfo(TempFile("$IBROKEN.mp4", [1, 2, 3])));
    }

    [OptInFact("QUICKCLIP_TEST_RECYCLE_BIN")]
    public void A_project_directory_can_be_recycled_and_restored()
    {
        string file = TempFile("project/clip.txt".Replace('/', Path.DirectorySeparatorChar), []);
        string folder = Path.GetDirectoryName(file)!;
        DateTime since = DateTime.UtcNow.AddSeconds(-1);
        Assert.True(RecycleBin.TrySend(folder));
        Assert.False(Directory.Exists(folder));
        var receipt = RecycleBin.FindLatest(folder, since);
        Assert.NotNull(receipt);
        RecycleBin.Restore(receipt, folder);
        Assert.True(File.Exists(file));
    }

    [OptInFact("QUICKCLIP_TEST_RECYCLE_BIN")]
    public void A_recycled_file_can_be_found_and_taken_back_out()
    {
        byte[] contents = Encoding.UTF8.GetBytes("the clip before it was edited " + Guid.NewGuid());
        string clip = TempFile("clip.mp4", contents);
        DateTime before = DateTime.UtcNow.AddSeconds(-2);
        Assert.True(RecycleBin.TrySend(clip), "Opt-in recovery test requires a working Recycle Bin on the temporary drive.");

        var recycled = RecycleBin.FindLatest(clip, before);
        Assert.NotNull(recycled);
        Assert.Equal(clip, recycled!.OriginalPath, ignoreCase: true);
        Assert.Null(RecycleBin.FindLatest(clip, DateTime.UtcNow.AddMinutes(1))); // only ones sent after the given time

        RecycleBin.Restore(recycled, clip);
        Assert.Equal(contents, File.ReadAllBytes(clip));
        Assert.False(File.Exists(recycled.InfoPath));
        Assert.Null(RecycleBin.FindLatest(clip, before));
    }
}
