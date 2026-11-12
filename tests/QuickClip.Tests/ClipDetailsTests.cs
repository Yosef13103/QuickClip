using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class ClipDetailsTests
{
    private static readonly DateTimeOffset Capture = new(2025, 6, 3, 18, 30, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Existing_records_upgrade_without_changing_original_details_or_media()
    {
        if (!AppProfile.IsPersonal) return;
        string media = Path.Combine(AppProfile.LocalDirectory, Guid.NewGuid().ToString("N") + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        File.WriteAllText(media, "fixture");
        var original = new ClipDetails(Capture, "Terraria", "Moon Lord");
        ClipDetailsStore.Write(media, original);

        // Remove the new fields to exercise the shape written by previous versions.
        var detailsDirectory = Path.Combine(AppProfile.LocalDirectory, "clip-details");
        var file = new FileInfo(media);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(file.FullName.ToUpperInvariant())));
        string metadata = Path.Combine(detailsDirectory, key + ".json");
        var legacy = JsonNode.Parse(File.ReadAllText(metadata))!;
        foreach (string field in new[] { "Tags", "IsFavorite", "Bookmarks" })
            legacy["Details"]!.AsObject().Remove(field);
        File.WriteAllText(metadata, legacy.ToJsonString());

        var read = ClipDetailsStore.Read(file, "Fallback", null);
        Assert.Equal(original, read);
        ClipDetailsStore.Write(media, read with
        {
            Tags = ["  Boss ", "boss", "音声"],
            IsFavorite = true,
            Bookmarks = [new(0.25, "  Start  ")],
        });
        var updated = ClipDetailsStore.Read(new FileInfo(media), "Fallback", null);
        Assert.Equal(Capture, updated.CapturedAt);
        Assert.Equal(original.Game, updated.Game);
        Assert.Equal(original.Title, updated.Title);
        Assert.Equal(new[] { "Boss", "音声" }, updated.Tags);
        Assert.True(updated.IsFavorite);
        Assert.Equal(new ClipBookmark(0.25, "Start"), Assert.Single(updated.Bookmarks));
        Assert.Equal("fixture", File.ReadAllText(media));

        File.AppendAllText(media, " replaced");
        Assert.False(ClipDetailsStore.Read(new FileInfo(media), "Fallback", null).IsFavorite);
    }

    [Fact]
    public void Invalid_metadata_is_filtered_and_tag_search_is_case_insensitive()
    {
        var details = new ClipDetails(Capture, "Terraria", "Boss in title")
        {
            Tags = ["", " combat\nlog ", new string('x', 33), " Funny "],
            Bookmarks = [new(double.NaN, "NaN"), new(-2, "Negative"), new(double.MaxValue, "Overflow"),
                new(2, "  "), new(4, "Four"), new(1, "One"), new(4, "Duplicate")],
        };
        var valid = details.Normalize();
        Assert.Equal("Funny", Assert.Single(valid.Tags));
        Assert.Equal(new[] { new ClipBookmark(1, "One"), new ClipBookmark(4, "Four") }, valid.Bookmarks);
        Assert.True(valid.MatchesOrganizationFilter("UNN", false));
        Assert.False(valid.MatchesOrganizationFilter("UNN", true));
        Assert.False(valid.MatchesOrganizationFilter("boss", false));
        Assert.True((valid with { IsFavorite = true }).MatchesOrganizationFilter("fun", true));
    }

    [Fact]
    public void Trimmed_export_inherits_details_and_rebases_only_bookmarks_inside_the_output()
    {
        var original = new ClipDetails(Capture, "Terraria", "Moon Lord")
        {
            Tags = ["boss"],
            IsFavorite = true,
            Bookmarks = [new(1, "Before"), new(2, "At start"), new(2.5, "Inside"), new(4, "At end"), new(5, "After")],
        };
        var exported = original.ForExport(2, 4);
        Assert.Equal(original with { Bookmarks = exported.Bookmarks }, exported);
        Assert.Equal(new[] { new ClipBookmark(0, "At start"), new ClipBookmark(0.5, "Inside") }, exported.Bookmarks);
        Assert.Equal(5, original.Bookmarks.Length);
    }

    [Fact]
    public async Task Details_form_preserves_saved_bookmarks_when_other_metadata_changes()
    {
        if (!AppProfile.IsPersonal) return;
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string media = Path.Combine(AppProfile.LocalDirectory, Guid.NewGuid().ToString("N") + ".mp4");
                Directory.CreateDirectory(Path.GetDirectoryName(media)!);
                File.WriteAllText(media, "fixture");
                double? sought = null;
                var editor = new ClipDetailsEditor { SeekBookmark = seconds => sought = seconds, KnownTags = () => ["boss", "chat"] };
                var bookmark = new ClipBookmark(1.75, "Boss arrives");
                var original = new ClipDetails(Capture, "Terraria") { Bookmarks = [bookmark] };
                ClipDetailsStore.Write(media, original);
                editor.Open(media, original, duration: 3);
                Assert.Contains(Path.GetFileName(media), editor.MediaSummary.Text);
                var row = Assert.IsType<DockPanel>(Assert.Single(editor.BookmarkRows.Children));
                Assert.IsType<Button>(row.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(bookmark.Seconds, sought);
                Assert.Equal(bookmark.Label, Assert.IsType<TextBlock>(row.Children[1]).Text);
                editor.TagBox.Text = "BO";
                var suggestion = Assert.IsType<Button>(Assert.Single(editor.TagSuggestions.Children));
                suggestion.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                editor.FavoriteBox.IsChecked = true;
                Assert.True(editor.HasDraft);
                editor.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(editor.HasDraft);

                var saved = ClipDetailsStore.Read(new FileInfo(media), "Fallback", null);
                Assert.Equal("boss", Assert.Single(saved.Tags));
                Assert.True(saved.IsFavorite);
                Assert.Equal(bookmark, Assert.Single(saved.Bookmarks));
                Assert.Equal(Capture, saved.CapturedAt);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
