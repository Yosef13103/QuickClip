using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuickClip.Gallery;

namespace QuickClip.Views;

public partial class StorageDashboard : UserControl
{
    private sealed record RootRow(string Path, string Summary, bool CanReveal);
    private CancellationTokenSource? _refresh;
    internal ILibraryInventorySource? InventorySource { get; set; }
    internal event Action? BackRequested;

    public StorageDashboard()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible) await RefreshAsync();
            else _refresh?.Cancel();
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    internal async Task RefreshAsync()
    {
        _refresh?.Cancel();
        InventoryContent.Visibility = Visibility.Collapsed;
        RefreshButton.IsEnabled = false;
        if (InventorySource is not { } source)
        {
            _refresh = null;
            Status.Text = "Enable indexing for your clips folder in Settings to see storage inventory.";
            return;
        }

        using var refresh = new CancellationTokenSource();
        _refresh = refresh;
        Status.Text = "Reading the library index…";
        try
        {
            var snapshot = await source.ReadAsync(refresh.Token);
            refresh.Token.ThrowIfCancellationRequested();
            var model = new StorageDashboardModel(snapshot);
            Totals.Text = $"{model.ClipCount} indexed clips · {model.TotalSize}";
            Availability.Text = model.Availability;
            CategoryRows.ItemsSource = model.Categories.ToArray();
            GameRows.ItemsSource = model.Games.ToArray();
            RootRows.ItemsSource = snapshot.Roots.Select(root =>
            {
                string free = root.FreeBytes is { } bytes ? $" · {Fmt.Size(bytes)} free" : "";
                return new RootRow(root.Path, root.State + free, root.State == InventoryRootState.Available);
            }).ToArray();
            NoRoots.Visibility = snapshot.Roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            InventoryContent.Visibility = Visibility.Visible;
            Status.Text = "";
        }
        catch (OperationCanceledException) when (refresh.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Write("Could not read indexed storage inventory: " + ex.Message);
            if (!refresh.IsCancellationRequested) Status.Text = "Couldn’t read the inventory. Try refreshing.";
        }
        finally
        {
            if (_refresh == refresh)
            {
                _refresh = null;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RootRow { CanReveal: true } row } ||
            ClipLibrary.IsMomentsPath(row.Path) || !Directory.Exists(row.Path))
        {
            Status.Text = "This folder is unavailable.";
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { row.Path } }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Status.Text = "Couldn’t open this folder in Explorer.";
        }
    }
}
