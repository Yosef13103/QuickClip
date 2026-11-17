using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class EditorView
{
    private ContextMenu? _videoContext;

    private bool CanOpenSavedClip(string path) => IsVisible && SourceReady && !_deletingMedia &&
        _exportCts == null && !_exportPreparing && EditorSource.MatchesLoadedPreview(CurrentFile, path) && VerifySource(path);

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentFile is { } path && CanOpenSavedClip(path))
            ExternalPlayerDialogs.Open(OwnerWindow, path, _settings.ExternalPlayerPath);
    }

    private void OpenVideoContext()
    {
        if (_closed || !IsVisible || CurrentFile is not { } path || !SourceReady || !_previewReady) return;
        if (_videoContext is { } old) old.IsOpen = false;
        var menu = new ContextMenu { PlacementTarget = Video, Placement = PlacementMode.MousePoint };
        _videoContext = menu;
        void Add(string label, Action action) => SharingDialogs.Add(menu.Items, label, () =>
        {
            if (CanOpenSavedClip(path)) action();
        });
        Add(_paused ? "Play" : "Pause", TogglePlay);
        Add("Mute / unmute", Volume.ToggleMute);
        Add(_fullscreen ? "Exit fullscreen" : "Fullscreen", () => SetFullscreen(!_fullscreen));
        menu.Items.Add(new Separator());
        Add("Open in " + ExternalPlayer.Label(_settings.ExternalPlayerPath), () =>
            ExternalPlayerDialogs.Open(OwnerWindow, path, _settings.ExternalPlayerPath));
        var players = new MenuItem { Header = "Open with…" };
        ExternalPlayerDialogs.Populate(players.Items, OwnerWindow, player =>
        {
            if (CanOpenSavedClip(path)) ExternalPlayerDialogs.Open(OwnerWindow, path, player);
        });
        menu.Items.Add(players);
        Add("Share saved clip", () => _ = ClipSharing.ShareAsync(path));
        Add("Show in Explorer", () =>
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add($"/select,{path}");
            Process.Start(start)?.Dispose();
        });
        menu.Closed += (_, _) => { if (ReferenceEquals(_videoContext, menu)) _videoContext = null; };
        menu.IsOpen = true;
    }
}
