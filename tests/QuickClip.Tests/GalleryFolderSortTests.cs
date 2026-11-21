using QuickClip.Gallery;

namespace QuickClip.Tests;

public sealed class GalleryFolderSortTests
{
    [Theory]
    [InlineData(true, false, "Alpha,bravo,Zulu")]
    [InlineData(true, true, "Zulu,bravo,Alpha")]
    [InlineData(false, false, "bravo,Zulu,Alpha")]
    [InlineData(false, true, "Alpha,Zulu,bravo")]
    public void Folders_support_both_sort_directions(bool name, bool descending, string expected)
    {
        var items = new[] { (Name: "Zulu", Date: new DateTime(2026, 1, 2)), (Name: "Alpha", Date: new DateTime(2026, 1, 3)), (Name: "bravo", Date: new DateTime(2026, 1, 1)) };
        Assert.Equal(expected, string.Join(",", GalleryFolderSort.Apply(items, item => item.Name, item => item.Date, name, descending).Select(item => item.Name)));
    }
}
