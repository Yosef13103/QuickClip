using System.Windows;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class GamesSettingsViewTests
{
    [Fact]
    public async Task Games_page_loads_at_a_typical_scaled_desktop_size()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var page = new GamesSettingsView();
                var size = new Size(1050, 660);
                page.Measure(size);
                page.Arrange(new Rect(size));
                Assert.True(page.Games.ActualHeight > 0);
                Assert.True(page.AutoScan.IsChecked == true);
                Assert.False(page.CancelButton.IsEnabled);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
