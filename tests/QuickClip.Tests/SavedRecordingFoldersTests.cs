using System.Windows;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class SavedRecordingFoldersTests : IDisposable
{
    private readonly string _fixture = Directory.CreateTempSubdirectory("qc-recording-roots-").FullName;

    [Fact]
    public void Fresh_and_legacy_settings_keep_the_same_destination()
    {
        var fresh = AppSettings.Parse("{}");
        Assert.Empty(SavedRecordingFolders.Additional(fresh));
        Assert.Equal(AppProfile.ClipsDirectory, fresh.ClipsFolder);
        string old = MakeFolder("old");
        var legacy = AppSettings.Parse(System.Text.Json.JsonSerializer.Serialize(new { ClipsFolder = old }));
        Assert.Equal(old, legacy.ClipsFolder);
        Assert.Empty(SavedRecordingFolders.Additional(legacy));
    }

    [Fact]
    public void Destination_switch_and_rollback_retain_the_other_folder_and_never_create_a_missing_source()
    {
        string old = Path.Combine(_fixture, "offline"), next = MakeFolder("new");
        var settings = new AppSettings { ClipsFolder = old };
        (settings.ClipsFolder, settings.AdditionalClipSources) = SavedRecordingFolders.PlanChange(settings, next);
        var reloaded = AppSettings.Parse(settings.ToJson());
        Assert.Equal(next, reloaded.ClipsFolder);
        Assert.Equal(old, Assert.Single(SavedRecordingFolders.Additional(reloaded)));
        Assert.False(Directory.Exists(old));
        Directory.CreateDirectory(old);
        (reloaded.ClipsFolder, reloaded.AdditionalClipSources) = SavedRecordingFolders.PlanChange(reloaded, old);
        Assert.Equal(old, reloaded.ClipsFolder);
        Assert.Equal(next, Assert.Single(SavedRecordingFolders.Additional(reloaded)));
    }

    [Fact]
    public async Task Both_roots_keep_clips_search_favorites_and_projects_when_destination_changes()
    {
        string old = MakeFolder("old"), next = MakeFolder("new");
        string oldClip = Path.Combine(old, "Old favorite.mp4"), newClip = Path.Combine(next, "New clip.mp4");
        File.WriteAllText(oldClip, "fixture");
        File.WriteAllText(newClip, "fixture");
        ProjectLibrary.Create(old, "Old project");
        ProjectLibrary.Create(next, "New project");
        var settings = new AppSettings { ClipsFolder = old };
        var index = new LibraryIndex(Path.Combine(_fixture, "library.db"));
        var oldId = await index.RegisterRootAsync(old);
        await index.ReconcileAsync(oldId);
        var original = Assert.Single(await index.QueryClipsAsync(new() { RootId = oldId }));
        await index.SetFavoriteAsync(original.Id, true);

        (settings.ClipsFolder, settings.AdditionalClipSources) = SavedRecordingFolders.PlanChange(settings, next);
        var newId = await index.RegisterRootAsync(settings.ClipsFolder);
        await index.ReconcileAsync(newId);
        Assert.True(SavedRecordingFolders.ContainsMedia(settings, oldClip));
        Assert.True(SavedRecordingFolders.ContainsMedia(settings, newClip));
        Assert.Equal(original.Id, Assert.Single(await index.QueryClipsAsync(new() { Search = "old favorite", Favorite = true })).Id);
        Assert.Equal(newClip, Assert.Single(await index.QueryClipsAsync(new() { RootId = newId })).Path);
        Assert.Equal("Old project", Assert.Single(ProjectLibrary.List(old)).Name);
        Assert.Equal("New project", Assert.Single(ProjectLibrary.List(next)).Name);
        Assert.Equal("fixture", File.ReadAllText(oldClip));
        Assert.Equal("fixture", File.ReadAllText(newClip));
        var nested = Directory.CreateDirectory(Path.Combine(old, "nested")).FullName;
        Assert.Throws<ArgumentException>(() => SavedRecordingFolders.PlanAdd(settings, nested));
    }

    [Fact]
    public async Task Settings_sections_keep_their_controls_and_can_switch_at_scaled_desktop_width()
    {
        if (!AppProfile.IsPersonal) return;
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var view = new SettingsView();
                var size = new Size(1050, 660);
                view.Measure(size);
                view.Arrange(new Rect(size));
                Assert.Equal(Visibility.Visible, view.RecordingCard.Visibility);
                view.LibrarySection.IsChecked = true;
                Assert.Equal(Visibility.Visible, view.LibraryCard.Visibility);
                Assert.Equal(Visibility.Collapsed, view.RecordingCard.Visibility);
                Assert.True(view.LibrarySection.Focusable);
                view.AudioSection.IsChecked = true;
                Assert.Equal(Visibility.Visible, view.AudioCard.Visibility);
                Assert.Equal(Visibility.Collapsed, view.LibraryCard.Visibility);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private string MakeFolder(string name) => Directory.CreateDirectory(Path.Combine(_fixture, name)).FullName;
    public void Dispose() => Directory.Delete(_fixture, recursive: true);
}
