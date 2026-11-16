using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using QuickClip.Gallery;
using QuickClip.Library;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class ClipDetailsAutosaveTests
{
    [Fact]
    public Task Two_tags_flush_once_when_storage_returns_tags_in_index_order() => OnDispatcher(async () =>
    {
        var actions = new CanonicalStore();
        var editor = new ClipDetailsEditor { AutoSaveEnabled = true, AnnotationActions = actions };
        editor.Open(Path.Combine(Path.GetTempPath(), "generated-clip.mp4"), new(DateTimeOffset.UtcNow, "Fixture"));
        AddTag(editor, "g");
        AddTag(editor, "fgf");

        Assert.True(await editor.FlushAsync());
        Assert.False(editor.HasDraft);
        Assert.Equal(1, actions.Writes);
    });

    [Fact]
    public Task Timed_out_write_remains_owned_and_late_completion_cannot_change_the_next_clip() => OnDispatcher(async () =>
    {
        var actions = new CanonicalStore();
        var stalled = new TaskCompletionSource<ClipAnnotationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        actions.WriteOverride = (_, _, _) => stalled.Task;
        var editor = new ClipDetailsEditor
        {
            AutoSaveEnabled = true, AnnotationActions = actions, SaveTimeout = TimeSpan.FromMilliseconds(120),
        };
        string path = Path.Combine(Path.GetTempPath(), "generated-a.mp4");
        string other = Path.Combine(Path.GetTempPath(), "generated-b.mp4");
        editor.Open(path, new(DateTimeOffset.UtcNow, "Fixture"));
        AddTag(editor, "g");
        int callbacks = 0;
        editor.AutoSaved += (_, _) => callbacks++;

        Task<bool> leaving = editor.FlushAsync();
        Assert.False(await leaving.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(actions.Token.IsCancellationRequested);
        Assert.False(await editor.FlushAsync());
        Assert.Equal(1, actions.Writes);
        Assert.True(editor.IsEnabled);
        Assert.True(editor.HasDraft);

        editor.Open(other, new(DateTimeOffset.UtcNow, "Second"));
        stalled.SetResult(new(path, new(DateTimeOffset.UtcNow, "Fixture") { Tags = ["g"] }, null));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(editor.IsSaving);
        Assert.Equal(0, callbacks);
        Assert.Equal(other, editor.AnnotationTarget?.Path);
        Assert.Empty(editor.AnnotationTarget!.Details.Tags);
    });

    [Fact]
    public Task Committed_metadata_updates_clean_fields_and_protects_local_changes() => OnDispatcher(() =>
    {
        var editor = new ClipDetailsEditor { AutoSaveEnabled = true };
        var stamp = new FileStamp(1, 2, 3, "fixture");
        var start = new ClipDetails(DateTimeOffset.UtcNow, "Game", "Original")
        {
            IndexId = Guid.NewGuid(), IndexRevision = 4,
        };
        editor.Open(Path.Combine(Path.GetTempPath(), "generated-clip.mp4"), start, source: stamp);
        var external = start with { Title = "Gallery title", IsFavorite = true, Tags = ["g"], IndexRevision = 5 };
        Assert.True(editor.ApplyCommittedDetails(external));
        var title = (TextBox)editor.FindName("TitleBox");
        Assert.Equal("Gallery title", title.Text);
        Assert.False(editor.HasDraft);
        title.Text = "Local title";
        Assert.False(editor.ApplyCommittedDetails(external with { Title = "Other title", IndexRevision = 6 }));
        Assert.Equal("Local title", title.Text);
        Assert.True(editor.HasDraft);
        Assert.Equal(stamp, editor.AnnotationTarget?.Source);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Recovery_restores_independent_fields_without_overwriting_newer_changes() => OnDispatcher(() =>
    {
        var source = new FileStamp(1, 2, 3, Guid.NewGuid().ToString("N"));
        var editor = new ClipDetailsEditor { AutoSaveEnabled = true };
        string path = Path.Combine(Path.GetTempPath(), "generated-recovery.mp4");
        var start = new ClipDetails(DateTimeOffset.UtcNow, "Fixture", "Original");
        editor.Open(path, start, source: source);
        AddTag(editor, "g");
        var draft = new ClipEditDraft(1, 3, 2, false, [], null);
        try
        {
            new EditorRecovery(source, editor.CaptureRecovery(), null, draft).Write(CancellationToken.None);
            Assert.Null(EditorRecovery.Read(source with { Modified = 3 }));
            var recovery = Assert.IsType<EditorRecovery>(EditorRecovery.Read(source));
            Assert.Equal(draft.Serialize(), recovery.Edit!.Serialize());
            editor.Open(path, start with { Title = "New title" }, source: source);
            Assert.True(editor.RestoreRecovery(recovery.Details!, apply: true));
            Assert.Equal("New title", ((TextBox)editor.FindName("TitleBox")).Text);
            Assert.True(editor.HasDraft);

            editor.Open(path, start with { Tags = ["external"] }, source: source);
            Assert.False(editor.RestoreRecovery(recovery.Details!, apply: true));
            Assert.False(editor.HasDraft);
        }
        finally { EditorRecovery.Remove(source); }
        return Task.CompletedTask;
    });

    private static void AddTag(ClipDetailsEditor editor, string tag)
    {
        ((TextBox)editor.FindName("TagBox")).Text = tag;
        var method = typeof(ClipDetailsEditor).GetMethod("AddTag_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        method.Invoke(editor, [editor, new RoutedEventArgs(ButtonBase.ClickEvent)]);
    }

    private sealed class CanonicalStore : IClipAnnotationActions
    {
        internal int Writes { get; private set; }
        internal CancellationToken Token { get; private set; }
        internal Func<ClipAnnotationTarget, ClipDetails, CancellationToken, Task<ClipAnnotationResult>>? WriteOverride { get; set; }

        public async Task<ClipAnnotationResult> SaveDraftAsync(ClipAnnotationTarget target, ClipDetails draft, CancellationToken token)
        {
            Writes++;
            Token = token;
            if (WriteOverride != null) return await WriteOverride(target, draft, token);
            await Task.Yield();
            // Bound the regression test on the original looping implementation.
            if (Writes > 3) return new(target.Path, null, "Test write limit reached.");
            return new(target.Path, draft with { Tags = draft.Tags.Order(StringComparer.OrdinalIgnoreCase).ToArray() }, null);
        }

        public Task<IReadOnlyList<ClipAnnotationResult>> ApplyAsync(IReadOnlyList<ClipAnnotationTarget> targets,
            string? addTag, bool? favorite, CancellationToken token) => throw new NotSupportedException();
    }

    private static async Task OnDispatcher(Func<Task> run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try { await run(); completion.SetResult(); }
                catch (Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
