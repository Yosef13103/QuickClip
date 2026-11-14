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
        public Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft,
            CancellationToken token)
        {
            Drafts.Add(draft);
            return Task.FromResult(new ClipAnnotationResult(target.Path, draft, null));
        }

        public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
            string? addTag, bool? favorite, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<ClipAnnotationResult>>([]);
    }

    [Fact]
    public async Task Gallery_autosave_debounces_title_game_edits_and_keeps_editor_open()
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

                    editor.Clear();
                    await Task.Delay(1000);
                    Assert.Single(actions.Drafts); // Clear() must never save a deleted/obsolete file.
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
