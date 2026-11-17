using System.Windows.Controls;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class GalleryMetadataAutosaveTests
{
    private sealed class SavedDrafts : IClipAnnotationActions
    {
        internal readonly List<ClipDetails> Drafts = [];
        internal Func<ClipAnnotationTarget, ClipDetails, Task<ClipAnnotationResult>>? Write;
        public Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft,
            CancellationToken token)
        {
            Drafts.Add(draft);
            return Write?.Invoke(target, draft) ?? Task.FromResult(new ClipAnnotationResult(target.Path, draft, null));
        }

        public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
            string? addTag, bool? favorite, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ClipAnnotationResult>>([]);
    }

    [Fact]
    public Task Gallery_autosave_debounces_title_game_edits_and_keeps_editor_open() => OnEditorThread(async (editor, actions, path) =>
    {

        int autosaved = 0, manuallySaved = 0;
        editor.AutoSaved += (_, _) => autosaved++;
        editor.Saved += (_, _) => manuallySaved++;
        editor.Open(path, new ClipDetails(DateTimeOffset.UtcNow, "Original"));
        Assert.Empty(actions.Drafts); // Open() must not persist its programmatic field changes.

        var title = Assert.IsType<TextBox>(editor.FindName("TitleBox"));
        var game = Assert.IsType<TextBox>(editor.FindName("GameBox"));
        title.Text = "Edited title";
        game.Text = "New game";
        await Task.Delay(1350);

        var saved = Assert.Single(actions.Drafts);
        Assert.Equal("Edited title", saved.Title);
        Assert.Equal("New game", saved.Game);
        Assert.Equal(1, autosaved);
        Assert.Equal(0, manuallySaved);
        Assert.False(editor.HasDraft);

        Assert.Equal(System.Windows.Visibility.Collapsed,
            Assert.IsType<Button>(editor.FindName("SaveButton")).Visibility);

        game.Text = "";
        await Task.Delay(1150);
        Assert.Equal(2, actions.Drafts.Count);
        Assert.Equal(actions.Drafts[1].Game, game.Text);
        Assert.False(editor.HasDraft); // Empty game resolves to canonical folder label.

        editor.Clear();
        await Task.Delay(1000);
        Assert.Equal(2, actions.Drafts.Count); // Clear() must never save a deleted/obsolete file.

    });

    [Fact]
    public Task Navigation_flush_waits_for_a_write_and_preserves_newer_input() => OnEditorThread(async (editor, actions, path) =>
    {
        var title = Assert.IsType<TextBox>(editor.FindName("TitleBox"));
        var pending = new TaskCompletionSource<ClipAnnotationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.Write = (_, _) => pending.Task;
        editor.Open(path, new ClipDetails(DateTimeOffset.UtcNow, "Original"));
        title.Text = "First edit";
        var flush = editor.FlushAsync();
        Assert.False(flush.IsCompleted);
        title.Text = "Latest edit";

        actions.Write = null;
        pending.SetResult(new(path, Assert.Single(actions.Drafts), null));
        Assert.True(await flush);
        Assert.Equal(new[] { "First edit", "Latest edit" }, actions.Drafts.Select(draft => draft.Title));
        Assert.False(editor.HasDraft);
    });

    [Fact]
    public Task Failed_autosave_keeps_input_for_retry() => OnEditorThread(async (editor, actions, path) =>
    {
        editor.Open(path, new ClipDetails(DateTimeOffset.UtcNow, "Original"));
        var title = Assert.IsType<TextBox>(editor.FindName("TitleBox"));
        title.Text = "Retained title";
        actions.Write = (_, _) => Task.FromResult(new ClipAnnotationResult(path, null, "Media unavailable."));
        Assert.False(await editor.FlushAsync());
        Assert.True(editor.HasDraft);
        Assert.Equal("Retained title", title.Text);
        Assert.Contains("Could not save", Assert.IsType<TextBlock>(editor.FindName("Feedback")).Text);

        actions.Write = null;
        Assert.True(await editor.FlushAsync());
        Assert.False(editor.HasDraft);
        Assert.Equal("Saved", Assert.IsType<TextBlock>(editor.FindName("Feedback")).Text);
    });

    private static async Task OnEditorThread(Func<ClipDetailsEditor, SavedDrafts, string, Task> run)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "quickclip-autosave-" + Guid.NewGuid().ToString("N") + ".mp4");
                try
                {
                    File.WriteAllBytes(path, [1]);
                    var actions = new SavedDrafts();
                    var editor = new ClipDetailsEditor { AutoSaveEnabled = true, AnnotationActions = actions };
                    await run(editor, actions, path);
                    result.SetResult();
                }
                catch (Exception ex) { result.SetException(ex); }
                finally
                {
                    try { File.Delete(path); } catch { }
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
