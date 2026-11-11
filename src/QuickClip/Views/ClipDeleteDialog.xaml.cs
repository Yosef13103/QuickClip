using System.Windows;
using System.Windows.Media;

namespace QuickClip.Views;

public partial class ClipDeleteDialog : Window
{
    public ClipDeleteDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => CancelButton.Focus();
    }

    internal static bool Confirm(Window? owner, IReadOnlyList<string> paths)
    {
        if (!AppSettings.Current.ConfirmClipDeletion) return true;
        var dialog = new ClipDeleteDialog { Owner = owner };
        dialog.Heading.Text = paths.Count == 1 ? "Move clip to the Recycle Bin?" : $"Move {paths.Count} clips to the Recycle Bin?";
        dialog.Files.Text = string.Join("\n", paths.Take(5).Select(Path.GetFileName));
        if (paths.Count > 5) dialog.Files.Text += $"\n… and {paths.Count - 5} more";
        if (owner == null) return dialog.ShowDialog() == true;

        // A separate owned surface also dims the native mpv child window.
        var origin = owner.PointToScreen(new Point(0, 0));
        var transform = PresentationSource.FromVisual(owner)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        origin = transform.Transform(origin);
        var shade = new Window
        {
            Owner = owner, ShowInTaskbar = false, ShowActivated = false,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(145, 0, 0, 0)),
            Left = origin.X, Top = origin.Y, Width = owner.ActualWidth, Height = owner.ActualHeight,
            IsHitTestVisible = false,
        };
        try
        {
            shade.Show();
            dialog.Owner = shade;
            return dialog.ShowDialog() == true;
        }
        finally { shade.Close(); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
