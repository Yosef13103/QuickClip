using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace QuickClip.Views;

internal static class SharingDialogs
{
    internal static void OpenMenu(FrameworkElement anchor, Action<SharingProfile> share, Action? shareSaved = null)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        Populate(menu.Items, Window.GetWindow(anchor) ?? Application.Current?.MainWindow, share, shareSaved);
        menu.IsOpen = true;
    }

    internal static void Populate(ItemCollection menu, Window? owner, Action<SharingProfile> share, Action? shareSaved = null)
    {
        var settings = AppSettings.Current;
        var selected = SharingProfiles.Default(settings);
        menu.Clear();
        foreach (var profile in SharingProfiles.Get(settings))
            Add(menu, profile + (profile.Id == selected.Id ? " (default)" : ""), () => share(profile));
        Add(menu, "Custom size…", () =>
        {
            string? text = ModalDialog.Ask(owner, "Custom share size (MB)", Format(selected.LimitMB), "Prepare share", false);
            if (text == null) return;
            if (SharingProfiles.TryLimit(text, out double limit)) share(new(Guid.Empty, "Custom", limit));
            else ModalDialog.Show(owner, "Enter a positive size up to 4000 MB.", "Invalid size");
        });
        if (shareSaved != null) Add(menu, "Share saved original with default profile", shareSaved);
        menu.Add(new Separator());
        Add(menu, "Manage sharing profiles…", () => Manage(owner));
    }

    internal static void Manage(Window? owner)
    {
        if (owner?.Content is not Panel) return;
        var settings = AppSettings.Current;
        var profiles = SharingProfiles.Get(settings).ToList();
        Guid defaultId = SharingProfiles.Default(settings).Id;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Choose your own file caps in MB. Receiving apps can impose separate limits.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });
        var list = new ListBox { MaxHeight = 185, MinHeight = 64 };
        AutomationProperties.SetName(list, "Sharing profiles");
        panel.Children.Add(list);
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(buttons);
        Refresh(defaultId);
        Button("Add…", () =>
        {
            if (profiles.Count == SharingProfiles.MaxProfiles) return;
            if (Edit(null) is { } added) { profiles.Add(added); Refresh(added.Id); }
        });
        Button("Edit…", () =>
        {
            if (list.SelectedIndex is int index && index >= 0 && Edit(profiles[index]) is { } edited)
            { profiles[index] = edited; Refresh(edited.Id); }
        });
        Button("Default", () =>
        {
            if (list.SelectedIndex < 0) return;
            defaultId = profiles[list.SelectedIndex].Id;
            Refresh(defaultId);
        });
        Button("Up", () => Move(-1));
        Button("Down", () => Move(1));
        Button("Delete", () =>
        {
            int index = list.SelectedIndex;
            if (index < 0 || profiles.Count <= 1) return;
            bool wasDefault = profiles[index].Id == defaultId;
            profiles.RemoveAt(index);
            if (wasDefault) defaultId = profiles[0].Id;
            Refresh(profiles[Math.Min(index, profiles.Count - 1)].Id);
        });
        if (ModalDialog.Run(owner, "Sharing profiles", panel, "Save", "Cancel", initialFocus: list) &&
            !SharingProfiles.TrySave(settings, profiles, defaultId))
            ModalDialog.Show(owner, "The profiles could not be saved. Your previous profiles are still active.", "Sharing profiles");

        void Refresh(Guid id)
        {
            list.ItemsSource = profiles.Select(profile => profile + (profile.Id == defaultId ? " (default)" : "")).ToArray();
            list.SelectedIndex = profiles.FindIndex(profile => profile.Id == id);
        }
        void Move(int offset)
        {
            int index = list.SelectedIndex;
            int next = index + offset;
            if (index < 0 || next < 0 || next >= profiles.Count) return;
            (profiles[index], profiles[next]) = (profiles[next], profiles[index]);
            Refresh(profiles[next].Id);
        }
        void Button(string title, Action click)
        {
            var button = new Button { Content = title, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 5, 8, 5) };
            button.Click += (_, _) => click();
            buttons.Children.Add(button);
        }
        SharingProfile? Edit(SharingProfile? profile)
        {
            var form = new StackPanel();
            var name = new TextBox { Text = profile?.Name ?? "", MaxLength = 64 };
            var size = new TextBox { Text = Format(profile?.LimitMB ?? SharingProfiles.Default(settings).LimitMB), MaxLength = 24 };
            form.Children.Add(new TextBlock { Text = "Name" });
            form.Children.Add(name);
            form.Children.Add(new TextBlock { Text = "Maximum size (MB)", Margin = new Thickness(0, 8, 0, 0) });
            form.Children.Add(size);
            AutomationProperties.SetName(name, "Name");
            AutomationProperties.SetName(size, "Maximum size (MB)");
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
            form.Children.Add(error);
            while (ModalDialog.Run(owner, profile == null ? "Add sharing profile" : "Edit sharing profile", form,
                "Apply", "Cancel", initialFocus: name))
            {
                string title = name.Text.Trim();
                if (SharingProfiles.ValidName(title) && !profiles.Any(other => other.Id != profile?.Id &&
                    string.Equals(other.Name, title, StringComparison.OrdinalIgnoreCase)) &&
                    SharingProfiles.TryLimit(size.Text, out double limit))
                    return new(profile?.Id ?? Guid.NewGuid(), title, limit);
                error.Text = "Enter a unique name and a positive size up to 4000 MB.";
            }
            return null;
        }
    }

    internal static void Add(ItemCollection menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Add(item);
    }

    internal static string Format(double value) => value.ToString("0.######", CultureInfo.CurrentCulture);
}
