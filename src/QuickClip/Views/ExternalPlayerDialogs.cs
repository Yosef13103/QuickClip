using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using QuickClip.Shell;

namespace QuickClip.Views;

internal static class ExternalPlayerDialogs
{
    internal static void Open(Window? owner, string path, string? executable)
    {
        try { Process.Start(ExternalPlayer.BuildStartInfo(path, executable))?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { ModalDialog.Show(owner, ex.Message, "Cannot open external player"); }
    }

    internal static void Populate(ItemCollection items, Window? owner, Action<string?> select)
    {
        items.Clear();
        var current = AppSettings.Current.ExternalPlayerPath;
        Add("Windows default app", null);
        var vlc = ExternalPlayer.FindVlc();
        if (vlc != null) Add("VLC media player", vlc);
        if (!string.IsNullOrWhiteSpace(current) && !string.Equals(current, vlc, StringComparison.OrdinalIgnoreCase))
            Add(ExternalPlayer.Label(current), current);
        items.Add(new Separator());
        SharingDialogs.Add(items, "Browse for player…", () =>
        {
            var dialog = new OpenFileDialog { Title = "Choose external player", Filter = "Applications (*.exe)|*.exe", CheckFileExists = true, Multiselect = false };
            if ((owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) != true) return;
            if (!ExternalPlayer.IsExecutablePath(dialog.FileName))
            {
                ModalDialog.Show(owner, "Choose an existing player executable other than QuickClip.", "External player");
                return;
            }
            select(dialog.FileName);
        });

        void Add(string label, string? player)
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = label },
                IsCheckable = true,
                IsChecked = string.Equals(current, player, StringComparison.OrdinalIgnoreCase),
                ToolTip = player,
            };
            item.Click += (_, _) => select(player);
            items.Add(item);
        }
    }

    internal static void ChooseDefault(FrameworkElement anchor, Action changed)
    {
        Window? owner = Window.GetWindow(anchor);
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        Populate(menu.Items, owner, player =>
        {
            AppSettings settings = AppSettings.Current;
            var old = settings.ExternalPlayerPath;
            settings.ExternalPlayerPath = player;
            if (!settings.TrySave())
            {
                settings.ExternalPlayerPath = old;
                ModalDialog.Show(owner, "Could not save the external player preference.", "External player");
            }
            changed();
        });
        menu.IsOpen = true;
    }
}
