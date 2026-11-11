using System.Windows;
using System.Windows.Controls;
using QuickClip.Controls;

namespace QuickClip.Tests;

public sealed class AnimatedRevealTests
{
    [Fact]
    public async Task Expanding_reserves_content_height_and_collapse_releases_it()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var reveal = new AnimatedReveal { Child = new Border { Height = 120 } };
                var sibling = new Border { Height = 30 };
                var stack = new StackPanel { Children = { reveal, sibling } };
                var available = new Size(240, 600);
                foreach (bool expanded in new[] { false, true, false, true })
                {
                    reveal.IsExpanded = expanded;
                    // An unloaded tree has no dispatcher layout pass to invalidate its parent.
                    stack.InvalidateMeasure();
                    stack.InvalidateArrange();
                    stack.Measure(available);
                    stack.Arrange(new Rect(available));
                    Assert.Equal(expanded ? 120 : 0, sibling.TranslatePoint(new Point(), stack).Y);
                    Assert.Equal(expanded, reveal.IsHitTestVisible);
                }
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
