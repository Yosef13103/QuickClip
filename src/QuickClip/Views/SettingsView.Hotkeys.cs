using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using QuickClip.Shell;

namespace QuickClip.Views;

public partial class SettingsView
{
    private sealed record HotkeyRow(RecordingCommand Command, string Title, Button Assign, Button Clear, TextBlock Status);
    private readonly List<HotkeyRow> _hotkeyRows = [];
    private HotkeyRow? _capturingHotkey;

    private void BuildHotkeyRows()
    {
        AddRow(RecordingCommand.SaveClip, "Save clip", "Save the current replay buffer.");
        if (AppProfile.IsPersonal)
        {
            AddRow(RecordingCommand.ToggleMaster, "Toggle recording",
                "Turn recording on or off. Turning it off expires manual display permissions.");
            AddRow(RecordingCommand.ToggleDesktop, "Toggle desktop recording",
                "Start or stop recording the selected display. Starting requires your confirmation.");
            AddRow(RecordingCommand.SwitchMonitor, "Switch to next authorized display",
                "Cycle through connected displays you already approved for this manual session. Switching starts a new replay buffer.");
        }
        ShowHotkeys();
    }

    private void AddRow(RecordingCommand command, string title, string description)
    {
        var section = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        section.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        section.Children.Add(new TextBlock { Text = description, Style = (Style)FindResource("Hint") });
        var actions = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var assign = new Button { MinWidth = 140 };
        var clear = new Button { Content = "Clear", Margin = new Thickness(6, 0, 0, 0) };
        var reset = new Button { Content = "Reset", Margin = new Thickness(6, 0, 0, 0) };
        var status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Style = (Style)FindResource("Hint") };
        AutomationProperties.SetName(clear, $"Clear {title} shortcut");
        AutomationProperties.SetName(reset, $"Reset {title} shortcut");
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        var row = new HotkeyRow(command, title, assign, clear, status);
        assign.Click += (_, _) => BeginHotkeyCapture(row);
        assign.PreviewKeyDown += (_, e) => CaptureHotkey(row, e);
        assign.LostKeyboardFocus += (_, _) =>
        {
            if (_capturingHotkey == row) CancelHotkeyCapture();
        };
        clear.Click += (_, _) => SetHotkey(row, default);
        reset.Click += (_, _) => SetHotkey(row, RecordingHotkeys.Default(command));
        actions.Children.Add(assign);
        actions.Children.Add(clear);
        actions.Children.Add(reset);
        section.Children.Add(actions);
        section.Children.Add(status);
        HotkeyRows.Children.Add(section);
        _hotkeyRows.Add(row);
    }

    private void ShowHotkeys()
    {
        foreach (var row in _hotkeyRows)
        {
            if (_capturingHotkey == row) continue;
            var key = RecordingHotkeys.Read(AppSettings.Current, row.Command);
            string binding = key.IsEmpty ? "Unbound" : key.ToString();
            row.Assign.Content = key.IsEmpty ? "Assign shortcut…" : binding;
            AutomationProperties.SetName(row.Assign, $"Change {row.Title} shortcut, {binding}");
            row.Clear.IsEnabled = !key.IsEmpty;
            row.Status.Text = key.IsEmpty ? HotkeyMessage(HotkeyRegistration.Unbound) :
                HotkeyMessage(App.Host?.HotkeyState(row.Command));
        }
    }

    private void BeginHotkeyCapture(HotkeyRow row)
    {
        if (App.Host == null)
        {
            row.Status.Text = HotkeyMessage(null);
            return;
        }
        if (!row.Assign.Focus()) return;
        _capturingHotkey = row;
        App.Host.SuspendHotkey();
        row.Assign.Content = "Press the keys…";
        row.Status.Text = "Escape cancels. Backspace clears.";
    }

    private void CaptureHotkey(HotkeyRow row, KeyEventArgs e)
    {
        if (_capturingHotkey != row) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            CancelHotkeyCapture();
            return;
        }
        e.Handled = true;
        if (e.IsRepeat || Hotkey.IsModifierKey(key)) return;
        if (key == Key.Escape) CancelHotkeyCapture();
        else if (key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) SetHotkey(row, default);
        else
        {
            var shortcut = Hotkey.FromWpf(Keyboard.Modifiers, key);
            if (!shortcut.IsEmpty) SetHotkey(row, shortcut);
        }
    }

    private void CancelHotkeyCapture()
    {
        _capturingHotkey = null;
        App.Host?.RegisterHotkey();
        ShowHotkeys();
    }

    private void SetHotkey(HotkeyRow row, Hotkey shortcut)
    {
        bool listening = _capturingHotkey != null;
        _capturingHotkey = null;
        var result = App.Host?.SetHotkey(row.Command, shortcut);
        // An invalid or duplicate chord is rejected before the host tries registration.
        if (listening && result is HotkeyRegistration.Invalid or HotkeyRegistration.Duplicate) App.Host?.RegisterHotkey();
        ShowHotkeys();
        if (result is not (HotkeyRegistration.Registered or HotkeyRegistration.Unbound))
            row.Status.Text = HotkeyMessage(result) + " Previous shortcut kept.";
    }

    internal static string HotkeyMessage(HotkeyRegistration? status) => status switch
    {
        HotkeyRegistration.Unbound => "No shortcut assigned.",
        HotkeyRegistration.Registered => "Registered with Windows.",
        HotkeyRegistration.Invalid => "Invalid key combination.",
        HotkeyRegistration.Duplicate => "Another QuickClip command uses this shortcut.",
        HotkeyRegistration.Unavailable => "Windows could not register this shortcut. It may be reserved or in use.",
        _ => "Windows registration status is unavailable.",
    };
}
