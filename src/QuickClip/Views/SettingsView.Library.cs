using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class SettingsView
{
    private sealed record ClipSourceRow(string Path, bool Available)
    {
        public override string ToString() => Available ? Path : $"{Path} · unavailable";
    }
    private int _folderRefresh;

#if DEBUG
    internal async Task ShowLibraryForRenderAsync()
    {
        LibrarySection.IsChecked = true;
        await RefreshRecordingFoldersAsync();
    }
#endif

    private async Task RefreshRecordingFoldersAsync()
    {
        int refresh = ++_folderRefresh;
        var settings = AppSettings.Current;
        string destination = settings.ClipsFolder;
        string[] sources = SavedRecordingFolders.Additional(settings);
        string? selected = (AdditionalSourcesList.SelectedItem as ClipSourceRow)?.Path;
        FolderBox.Text = destination;
        RecordingFreeSpace.Text = "Checking free space…";
        var result = await Task.Run(() =>
        {
            var rows = sources.Select(path => new ClipSourceRow(path,
                LibraryFiles.InspectPath(path, directory: true) == ClipAvailability.Available)).ToArray();
            string space = "Free space unavailable.";
            try
            {
                if (Path.GetPathRoot(destination) is { } drive && new DriveInfo(drive) is { IsReady: true } volume)
                    space = $"{Fmt.Size(volume.AvailableFreeSpace)} free on the destination drive";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }
            return (rows, space);
        });
        if (refresh != _folderRefresh) return;
        RecordingFreeSpace.Text = result.space;
        AdditionalSourcesList.ItemsSource = result.rows;
        AdditionalSourcesList.SelectedItem = result.rows.FirstOrDefault(row => SavedRecordingFolders.Paths.Equals(row.Path, selected));
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var dialog = new OpenFolderDialog { Title = "Choose a saved recordings location", InitialDirectory = settings.ClipsFolder };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            if (ClipLibrary.IsMomentsPath(dialog.FolderName))
                throw new ArgumentException("Choose a recordings folder outside the Moments archive.");
            var plan = AppProfile.IsPersonal ? SavedRecordingFolders.PlanChange(settings, dialog.FolderName)
                : (Destination: dialog.FolderName, Sources: settings.AdditionalClipSources);
            if (SavedRecordingFolders.Paths.Equals(plan.Destination, settings.ClipsFolder)) return;
            if (AppProfile.IsPersonal && !ConfirmFolderChange(
                "New recordings will use the selected folder. The previous location will stay in Gallery as an additional source, and its files will stay in place.",
                "Change saved recordings location?", "Change and keep old folder")) return;
            if (SaveRecordingFolders(plan.Destination, plan.Sources)) await RefreshRecordingFoldersAsync();
        }
        catch (ArgumentException ex) { ShowFolderProblem(ex.Message); }
    }

    private async void AddClipSource_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var dialog = new OpenFolderDialog { Title = "Add an existing recordings folder to Gallery" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string[] sources = SavedRecordingFolders.PlanAdd(settings, dialog.FolderName);
            if (SaveRecordingFolders(settings.ClipsFolder, sources)) await RefreshRecordingFoldersAsync();
        }
        catch (ArgumentException ex) { ShowFolderProblem(ex.Message); }
    }

    private async void RemoveClipSource_Click(object sender, RoutedEventArgs e)
    {
        if (AdditionalSourcesList.SelectedItem is not ClipSourceRow row) return;
        if (!ConfirmFolderChange("Remove this folder from Gallery? The recordings and their metadata will stay in place.",
            "Remove Gallery source?", "Remove from Gallery")) return;
        var settings = AppSettings.Current;
        var sources = SavedRecordingFolders.Additional(settings)
            .Where(path => !SavedRecordingFolders.Paths.Equals(path, row.Path)).ToArray();
        if (SaveRecordingFolders(settings.ClipsFolder, sources)) await RefreshRecordingFoldersAsync();
    }

    private void OpenRecordingFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = AppSettings.Current.ClipsFolder;
        if (!Directory.Exists(path))
        {
            ShowFolderProblem("The saved recordings folder is unavailable.");
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path } }); }
        catch (System.ComponentModel.Win32Exception) { ShowFolderProblem("Couldn’t open the folder in Explorer."); }
    }

    private bool ConfirmFolderChange(string message, string title, string action) =>
        ModalDialog.AskConfirmation(Window.GetWindow(this), message, title, action);

    private void ShowFolderProblem(string message) =>
        ModalDialog.Show(Window.GetWindow(this), message, "Saved recordings", MessageBoxButton.OK, MessageBoxImage.Warning);

    private bool SaveRecordingFolders(string destination, string[] sources)
    {
        var settings = AppSettings.Current;
        var previous = (settings.ClipsFolder, settings.AdditionalClipSources);
        settings.ClipsFolder = destination;
        settings.AdditionalClipSources = sources;
        if (settings.TrySave()) return true;
        (settings.ClipsFolder, settings.AdditionalClipSources) = previous;
        ShowFolderProblem("The folder settings could not be saved. The previous locations are still active.");
        return false;
    }
}
