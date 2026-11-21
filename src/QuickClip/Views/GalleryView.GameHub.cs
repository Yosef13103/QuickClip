using System.Windows;
using QuickClip.Gallery;
using QuickClip.Library;

namespace QuickClip.Views;

public partial class GalleryView
{
    private void UpdateGameHub(IReadOnlyList<ClipItem> clips)
    {
        GameHub.Visibility = PersonalAnnotations && PersonalLibrary.Enabled && !_folderSelection.Game.IsAll
            ? Visibility.Visible : Visibility.Collapsed;
        if (GameHub.Visibility != Visibility.Visible) return;
        HubClips.Text = clips.Count.ToString("N0");
        HubStorage.Text = Fmt.Size(clips.Sum(c => c.Size));
        HubCollections.Text = clips.Select(c => c.Details.Collection?.Id).OfType<Guid>().Distinct().Count().ToString("N0");
        GameHub.ToolTip = "Matching clips, storage and folders.";
    }
}
