using System.Windows;
using QuickClip.Gallery;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class ClipDetailsEditorClearTests
{
    [Fact]
    public async Task Clearing_missing_source_forgets_annotation_target_without_touching_media()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                string missingFile = Path.Combine(Path.GetTempPath(), "quickclip-missing-editor-fixture-" +
                    Guid.NewGuid().ToString("N") + ".mp4");
                Assert.False(File.Exists(missingFile));
                var editor = new ClipDetailsEditor();
                editor.Open(missingFile, new ClipDetails(DateTimeOffset.Now, "SyntheticGame"));
                Assert.NotNull(editor.AnnotationTarget);
                editor.Clear();
                Assert.Null(editor.AnnotationTarget);
                Assert.False(editor.HasDraft);
                Assert.False(File.Exists(missingFile));
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
