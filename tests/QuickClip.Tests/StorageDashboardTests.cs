using System.Windows;
using QuickClip.Views;

namespace QuickClip.Tests;

[Collection("WPF controls")]
public sealed class StorageDashboardTests
{
    private static LibraryInventorySnapshot Sample() => new(
        [new(@"X:\Fixture", InventoryRootState.Available, 4096), new(@"Y:\Offline", InventoryRootState.Unavailable, null)],
        [new(InventoryCategory.Finished, "Terraria", 2, 2048), new(InventoryCategory.Originals, "Terraria", 1, null)],
        new HashSet<InventoryCategory> { InventoryCategory.Finished, InventoryCategory.Originals, InventoryCategory.Exports },
        MissingClips: 1, UnavailableClips: 4);

    [Fact]
    public void Distinguishes_unknown_partial_and_unreported_sizes()
    {
        var model = new StorageDashboardModel(Sample());
        Assert.Equal("3", model.ClipCount);
        Assert.Equal($"{Fmt.Size(2048)} known", model.TotalSize);
        Assert.Equal("1 missing · 4 unavailable", model.Availability);
        Assert.Equal("Unknown", model.Categories.Single(row => row.Label == "Originals").Size);
        Assert.Equal("Not reported", model.Categories.Single(row => row.Label == "Project-linked files").Size);
        Assert.Equal(Fmt.Size(0), model.Categories.Single(row => row.Label == "Exports").Size);
        Assert.Equal("Terraria", Assert.Single(model.Games).Label);
    }

    [Fact]
    public void Project_references_do_not_inflate_indexed_media_totals()
    {
        var snapshot = Sample();
        var model = new StorageDashboardModel(snapshot with
        {
            Groups = [.. snapshot.Groups, new(InventoryCategory.ProjectLinked, "Terraria", 2, 2048)],
            MeasuredCategories = new HashSet<InventoryCategory>(snapshot.MeasuredCategories) { InventoryCategory.ProjectLinked },
        });
        Assert.Equal("3", model.ClipCount);
        Assert.Equal($"{Fmt.Size(2048)} known", model.TotalSize);
        Assert.Equal("2", model.Categories.Single(row => row.Label == "Project-linked files").Clips);
    }

    [Fact]
    public async Task Wpf_dashboard_distinguishes_disconnected_and_indexed_states()
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var dashboard = new StorageDashboard();
                dashboard.RefreshAsync().GetAwaiter().GetResult();
                Assert.False(dashboard.RefreshButton.IsEnabled);
                Assert.Equal(Visibility.Collapsed, dashboard.InventoryContent.Visibility);
                var service = new FakeInventorySource();
                dashboard.InventorySource = service;
                dashboard.RefreshAsync().GetAwaiter().GetResult();
                Assert.True(dashboard.RefreshButton.IsEnabled);
                Assert.Equal(Visibility.Visible, dashboard.InventoryContent.Visibility);
                Assert.Equal(2, dashboard.RootRows.Items.Count);
                Assert.Contains("3 indexed clips", dashboard.Totals.Text);
                Assert.Equal(1, service.Reads);
                result.SetResult();
            }
            catch (Exception ex) { result.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await result.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class FakeInventorySource : ILibraryInventorySource
    {
        public int Reads { get; private set; }

        public Task<LibraryInventorySnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult(Sample());
        }
    }
}
