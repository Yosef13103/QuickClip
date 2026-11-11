using System.Windows;
using System.Windows.Controls;
using QuickClip.Gallery;

namespace QuickClip.Views;

public partial class ClipDetailsEditor : UserControl
{
    private string? _path;
    private ClipDetails? _details;
    private (long Length, DateTime Modified, DateTime Created) _fileStamp;

    public ClipDetailsEditor() => InitializeComponent();
    internal event Action<ClipDetails>? Saved;
    internal event Action? Cancelled;
    internal bool HasDraft => _details is { } details &&
        (TitleBox.Text.Trim() != (details.Title ?? "") || GameBox.Text.Trim() != details.Game);

    internal void Open(string path, ClipDetails details, bool focus = false)
    {
        _path = path;
        _details = details;
        var file = new FileInfo(path);
        _fileStamp = file.Exists ? (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) : default;
        CaptureDate.Text = $"Captured {details.CapturedAt.LocalDateTime:g}";
        TitleBox.Text = details.Title ?? "";
        GameBox.Text = details.Game;
        Feedback.Text = "";
        if (focus) TitleBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null || _details == null) return;
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || (file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) != _fileStamp)
            {
                Feedback.Text = "The file changed. Reopen it before saving details.";
                return;
            }
            string title = TitleBox.Text.Trim();
            string game = GameBox.Text.Trim();
            var details = _details with
            {
                Title = title.Length == 0 ? null : title,
                Game = game.Length == 0 ? ClipLibrary.InitialGame(AppSettings.Current.ClipsFolder, _path) : game,
            };
            ClipDetailsStore.Write(_path, details);
            Open(_path, details);
            Feedback.Text = "Details saved";
            Saved?.Invoke(details);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Feedback.Text = ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null && _details != null) Open(_path, _details);
        Cancelled?.Invoke();
    }
}
