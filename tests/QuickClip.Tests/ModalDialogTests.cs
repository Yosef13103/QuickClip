using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class ModalDialogTests
{
    [Fact]
    public async Task Cancel_and_confirm_return_distinct_results_and_remove_the_overlay()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var host = new Grid();
                var owner = new Window
                {
                    Content = host, Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
                };
                owner.Show();
                try
                {
                    foreach (bool accept in new[] { false, true })
                    {
                        owner.Dispatcher.BeginInvoke(() =>
                        {
                            var overlay = (Grid)host.Children[0];
                            var panel = (StackPanel)((Border)overlay.Children[0]).Child;
                            var buttons = (StackPanel)panel.Children[^1];
                            Assert.Equal("Keep clips", ((Button)buttons.Children[0]).Content);
                            Assert.Equal("Move to Recycle Bin", ((Button)buttons.Children[1]).Content);
                            ((Button)buttons.Children[accept ? 1 : 0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }, DispatcherPriority.Input);
                        Assert.Equal(accept, ModalDialog.ConfirmDelete(owner, "Delete test", "fixture.mp4"));
                        Assert.Empty(host.Children);
                    }
                }
                finally { owner.Close(); }
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
