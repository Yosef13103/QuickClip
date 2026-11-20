using System.Windows;
using System.Windows.Controls;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    private bool _sessionMode;
    private GallerySessionKey? _activeSession;
    private GalleryFolderSelection? _sessionFolder;

    private IReadOnlyList<ClipItem> ApplySessionView(IReadOnlyList<ClipItem> clips, TimeZoneInfo zone, out bool overview)
    {
        overview = false;
        bool enabled = PersonalAnnotations && PersonalLibrary.Enabled;
        SessionNavigation.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        GameHub.Visibility = enabled && !_folderSelection.Game.IsAll ? Visibility.Visible : Visibility.Collapsed;
        if (!enabled) return clips;
        if (_sessionFolder != _folderSelection)
        {
            _activeSession = null;
            _sessionFolder = _folderSelection;
        }
        var sessions = _sessionMode || !_folderSelection.Game.IsAll
            ? GallerySessions.Build(_clips, clips, zone) : [];
        if (GameHub.Visibility == Visibility.Visible)
        {
            HubClips.Text = clips.Count.ToString("N0");
            HubSessions.Text = sessions.Length.ToString("N0");
            HubStorage.Text = Fmt.Size(clips.Sum(c => c.Size));
            HubCollections.Text = clips.Select(c => c.Details.Collection?.Id).OfType<Guid>().Distinct().Count().ToString("N0");
            GameHub.ToolTip = "Counts and storage refer to currently matching clips. Historical session counts may be approximate.";
        }
        ClipsModeButton.IsEnabled = _sessionMode;
        SessionsModeButton.IsEnabled = !_sessionMode;
        SessionBackButton.Visibility = Visibility.Collapsed;
        SessionInfo.Text = "";
        if (!_sessionMode) return clips;

        var selected = sessions.FirstOrDefault(s => s.Key == _activeSession);
        if (selected != null)
        {
            SessionBackButton.Visibility = Visibility.Visible;
            SessionInfo.Text = selected.Heading + " · " + selected.Context;
            FolderInfo.Text = selected.Summary;
            return selected.Clips;
        }
        _activeSession = null;
        overview = true;
        SessionInfo.Text = $"{sessions.Length:N0} sessions · Choose a session to browse its clips";
        Rows.ItemsSource = sessions;
        return clips;
    }

    private void ClipsMode_Click(object sender, RoutedEventArgs e) => ChangeSessionMode(false);
    private void SessionsMode_Click(object sender, RoutedEventArgs e) => ChangeSessionMode(true);
    private void ChangeSessionMode(bool sessions)
    {
        _sessionMode = sessions;
        _activeSession = null;
        _renderedColumns = 0;
        StopHoverPreview();
        ShowFolder();
    }
    private void SessionBack_Click(object sender, RoutedEventArgs e)
    {
        _activeSession = null;
        _renderedColumns = 0;
        ShowFolder();
    }
    private void SessionCard_Click(object sender, RoutedEventArgs e)
    {
        if (!_sessionMode || sender is not FrameworkElement { DataContext: GallerySessionCard card }) return;
        _activeSession = card.Key;
        _renderedColumns = 0;
        ShowFolder();
    }
}
