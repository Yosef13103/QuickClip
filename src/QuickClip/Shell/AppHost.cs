using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using QuickClip.Discovery;
using QuickClip.Recording;
using QuickClip.Updates;
using QuickClip.Views;

namespace QuickClip.Shell;

/// <summary>
/// The part of QuickClip that lives while no window is open: a hidden window that owns the tray icon and
/// the global "save clip" hotkey, and drives the recorder.
/// </summary>
internal sealed class AppHost : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int TrayCallback = 0x8000 + 1; // WM_APP + 1
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private HwndSource? _window;
    private IntPtr _icon;
    private bool _iconAdded;
    private DispatcherTimer? _updateTimer;
    private bool _sourceBalloonShown;
    private string? _lastSourceGuidance;
    private bool _updateBalloonShown;   // the last notification was about an update
    private AppVersion? _notifiedVersion;
    private readonly ClipNotificationOverlay _clipOverlay = new();
    private Dictionary<RecordingCommand, HotkeyRegistration> _hotkeys = [];
    private bool _confirmingDesktop;

    public event Action? OpenRequested;
    /// <summary>The user clicked the "update available" notification.</summary>
    public event Action? UpdateRequested;
    public event Action? RecordingSettingsRequested;
    public event Action? QuitRequested;

    /// <summary>True if the configured shortcut is registered (false if another app already uses it).</summary>
    public bool HotkeyActive => HotkeyState(RecordingCommand.SaveClip) == HotkeyRegistration.Registered;
    internal HotkeyRegistration HotkeyState(RecordingCommand command) => _hotkeys.GetValueOrDefault(command);

    public void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("QuickClip host")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never shown
        });
        _window.AddHook(WndProc);
        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out _icon, 1);
        AddTrayIcon();
        RegisterHotkey();

        var recorder = Recorder.Instance;
        recorder.ReplaySaved += OnReplaySaved;
        recorder.SaveFailed += OnSaveFailed;
        recorder.StateChanged += OnRecorderState;
        _ = recorder.ApplySettingsAsync();
        if (AppProfile.IsPersonal) GameCatalogRuntime.Discovery.Start();

        if (!Updater.IsEnabled) return;
        // Look for a new release shortly after start (not during it), then once a day while running.
        _updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background, async (_, _) =>
        {
            _updateTimer!.Interval = TimeSpan.FromHours(24);
            if (AppSettings.Current.CheckForUpdates) await CheckForUpdateAsync();
        }, _dispatcher);
    }

    private async Task CheckForUpdateAsync()
    {
        var release = await Updater.CheckAsync();
        if (release == null || release.Version == _notifiedVersion) return;
        _notifiedVersion = release.Version;
        Notify("Update available", $"QuickClip {release.Version.Display} is ready to install. Click to see what's new.");
        _updateBalloonShown = true;
    }

    /// <summary>Re-reads the shortcut from settings. Returns false if it's taken.</summary>
    public bool RegisterHotkey()
    {
        if (_window == null) return false;
        SuspendHotkey();
        _hotkeys = RecordingHotkeys.Register(AppSettings.Current, AppProfile.IsPersonal, (command, key) =>
            RegisterHotKey(_window.Handle, (int)command, (uint)key.Modifiers | 0x4000 /* MOD_NOREPEAT */, (uint)key.VirtualKey));
        UpdateTooltip();
        return HotkeyActive;
    }

    /// <summary>Lets the settings page capture key presses without triggering a save.</summary>
    public void SuspendHotkey()
    {
        if (_window != null)
            foreach (var (command, status) in _hotkeys)
                if (status == HotkeyRegistration.Registered) UnregisterHotKey(_window.Handle, (int)command);
        _hotkeys.Clear();
    }

    internal HotkeyRegistration SetHotkey(RecordingCommand command, Hotkey key)
    {
        if (_window == null || !Enum.IsDefined(command) || (!AppProfile.IsPersonal && command != RecordingCommand.SaveClip))
            return HotkeyRegistration.Unavailable;
        var settings = AppSettings.Current;
        if (key.IsEmpty) key = default;
        else
        {
            if (!RecordingHotkeys.IsValid(key)) return HotkeyRegistration.Invalid;
            if (AppProfile.IsPersonal && Enum.GetValues<RecordingCommand>()
                .Any(other => other != command && RecordingHotkeys.Read(settings, other) == key))
                return HotkeyRegistration.Duplicate;
        }
        var old = RecordingHotkeys.Read(settings, command);
        RecordingHotkeys.Write(settings, command, key);
        RegisterHotkey();
        var result = HotkeyState(command);
        if (result is HotkeyRegistration.Registered or HotkeyRegistration.Unbound) settings.Save();
        else
        {
            RecordingHotkeys.Write(settings, command, old);
            RegisterHotkey();
        }
        return result;
    }

    public void Notify(string title, string text)
    {
        _updateBalloonShown = _sourceBalloonShown = false;
        var data = NewIconData();
        data.uFlags = NIF_INFO;
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void OnReplaySaved(ReplaySaveReceipt receipt) => _dispatcher.BeginInvoke(() =>
    {
        string label = AppProfile.IsPersonal
            ? receipt.GameContext is { } game ? $"{game} · {receipt.SourceDescription}" : receipt.SourceDescription
            : receipt.Game;
        string message = receipt.ActualVideoDuration is { } actual
            ? $"{label} · {Fmt.CompactDuration(actual.TotalSeconds)} saved" : label;
        if (receipt.IsShorterThanRequested == true) message += $" ({Fmt.CompactDuration(receipt.RequestedSeconds)} requested)";
        ShowClipFeedback(success: true, message);
    });

    private void OnRecorderState(RecorderState state) => _dispatcher.BeginInvoke(() =>
    {
        if (_window == null) return;
        UpdateTooltip();
        string? guidance = state.Running ? null : SourceGuidance(state.Error);
        if (guidance != null && guidance != _lastSourceGuidance)
            NotifySource("Recording source unavailable", guidance);
        _lastSourceGuidance = guidance;
    });

    private static string? SourceGuidance(string? error) => AppProfile.IsPersonal ? error : null;

    private void NotifySource(string title, string message)
    {
        Notify(title, message);
        _sourceBalloonShown = true;
    }

    private void OnSaveFailed(string error) => _dispatcher.BeginInvoke(() =>
    {
        if (AppProfile.IsPersonal && !Recorder.Instance.IsRunning)
            NotifySource("Clip not saved", SourceGuidance(Recorder.Instance.Error) ?? error);
        else ShowClipFeedback(success: false, error);
    });

    internal void ShowClipFeedback(bool success, string message)
    {
        if (_window == null) return;
        var settings = AppSettings.Current;
        var feedback = ClipFeedback.From(settings, success, message);
        if (feedback.Sound) ClipSound.Play();
        if (feedback.Windows) Notify(feedback.Title, feedback.Message);
        if (feedback.Overlay)
        {
            try { _clipOverlay.Show(settings.ClipOverlay, feedback.Success); }
            catch (Exception ex) { Log.Write("Clip indicator unavailable: " + ex.GetType().Name); }
        }
    }

    internal void TestClipOverlay()
    {
        try { _clipOverlay.Show(AppSettings.Current.ClipOverlay ?? new(), success: true); }
        catch (Exception ex) { Log.Write("Clip indicator preview unavailable: " + ex.GetType().Name); }
    }

    private void UpdateTooltip()
    {
        if (!_iconAdded) return;
        var r = Recorder.Instance;
        string state = r.IsRunning
            ? $"Recording · {new Hotkey(AppSettings.Current.HotkeyModifiers, AppSettings.Current.HotkeyKey)} saves a clip"
            : r.Error != null ? "Not recording: " + r.Error
            : r.State.Mode == RecordingMode.Armed ? "Armed · no recording active" : "Recording is off";
        var data = NewIconData();
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        data.szTip = Truncate(AppProfile.Name + " — " + state, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private void AddTrayIcon()
    {
        var data = NewIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = TrayCallback;
        data.hIcon = _icon;
        data.szTip = AppProfile.Name;
        _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);
        data.uTimeoutOrVersion = 4; // NOTIFYICON_VERSION_4: event in LOWORD(lParam)
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
        UpdateTooltip();
    }

    private NOTIFYICONDATAW NewIconData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window?.Handle ?? IntPtr.Zero,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var command = (RecordingCommand)wParam.ToInt32();
            var chord = lParam.ToInt32();
            if (HotkeyState(command) == HotkeyRegistration.Registered &&
                RecordingHotkeys.Read(AppSettings.Current, command) == new Hotkey(chord & 0xF, (chord >> 16) & 0xFFFF))
            {
                RunRecordingCommand(command);
                handled = true;
            }
        }
        else if (msg == TrayCallback)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case 0x0202: // WM_LBUTTONUP
                    OpenRequested?.Invoke();
                    break;
                case 0x0405: // NIN_BALLOONUSERCLICK
                    if (_sourceBalloonShown) RecordingSettingsRequested?.Invoke();
                    else if (_updateBalloonShown) UpdateRequested?.Invoke();
                    else OpenRequested?.Invoke();
                    break;
                case 0x007B: // WM_CONTEXTMENU
                case 0x0205: // WM_RBUTTONUP
                    ShowMenu();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated)
        {
            AddTrayIcon(); // Explorer restarted
        }
        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        var recorder = Recorder.Instance;
        var s = AppSettings.Current;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        menu.Items.Add(Item("Open " + AppProfile.Name, () => OpenRequested?.Invoke(), bold: true));
        if (!recorder.IsRunning && SourceGuidance(recorder.Error) is { } guidance)
        {
            var sourceItem = Item("Recording source unavailable · Settings…", () => RecordingSettingsRequested?.Invoke());
            sourceItem.ToolTip = guidance;
            menu.Items.Add(sourceItem);
        }
        menu.Items.Add(Item($"Save clip ({new Hotkey(s.HotkeyModifiers, s.HotkeyKey)})",
            () => RunRecordingCommand(RecordingCommand.SaveClip), enabled: recorder.IsRunning));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(s.RecordingEnabled ? "Turn recording off" : "Turn recording on",
            () => RunRecordingCommand(RecordingCommand.ToggleMaster)));
        if (AppProfile.IsPersonal)
        {
            menu.Items.Add(Item(recorder.ManualDesktopRequested ? "Stop manual desktop capture" : "Start manual desktop capture…",
                () => RunRecordingCommand(RecordingCommand.ToggleDesktop), enabled: s.RecordingEnabled));
            if (recorder.ManualDesktopRequested)
            {
                menu.Items.Add(Item("Switch to next authorized display", () => RunRecordingCommand(RecordingCommand.SwitchMonitor)));
                var authorize = new MenuItem { Header = "Authorize and switch display…" };
                foreach (var monitor in Monitors.List().Where(m => m.CaptureIdentity != null))
                    authorize.Items.Add(Item(monitor.Label, () => ConfirmManualDisplay(recorder, monitor)));
                authorize.IsEnabled = authorize.Items.Count > 0;
                menu.Items.Add(authorize);
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", () => QuitRequested?.Invoke()));
        if (_window != null) SetForegroundWindow(_window.Handle); // so the menu closes when clicking elsewhere
        menu.IsOpen = true;
    }

    private void RunRecordingCommand(RecordingCommand command)
    {
        var recorder = Recorder.Instance;
        var settings = AppSettings.Current;
        switch (command)
        {
            case RecordingCommand.SaveClip:
                _ = recorder.SaveClipAsync();
                break;
            case RecordingCommand.ToggleMaster:
                settings.RecordingEnabled = !settings.RecordingEnabled;
                settings.Save();
                _ = recorder.ApplySettingsAsync();
                break;
            case RecordingCommand.ToggleDesktop when AppProfile.IsPersonal:
                if (settings.RecordingEnabled) ToggleManualDesktop(recorder);
                else NotifySource("Recording is off", "Enable QuickClip recording before starting manual desktop capture.");
                break;
            case RecordingCommand.SwitchMonitor when AppProfile.IsPersonal:
                _ = SwitchMonitorAsync(recorder);
                break;
        }
    }

    private async Task SwitchMonitorAsync(Recorder recorder)
    {
        if (!await recorder.CycleManualDisplayAsync())
            NotifySource("No other authorized display", "To switch, authorize another connected display in the manual desktop capture tray menu.");
    }

    private void ToggleManualDesktop(Recorder recorder)
    {
        if (recorder.ManualDesktopRequested)
        {
            _ = recorder.SetManualDesktopAsync(null);
            return;
        }
        var selected = CaptureSourcePolicy.SelectMonitor(Monitors.List(), AppSettings.Current.RecordMonitor, requireSelected: true);
        if (selected?.CaptureIdentity == null || string.IsNullOrWhiteSpace(AppSettings.Current.RecordMonitor))
        {
            NotifySource("Choose a display", "Select a connected display in Recording settings before starting desktop capture.");
            RecordingSettingsRequested?.Invoke();
            return;
        }
        ConfirmManualDisplay(recorder, selected);
    }

    private void ConfirmManualDisplay(Recorder recorder, MonitorInfo selected)
    {
        if (selected.CaptureIdentity == null || !AppSettings.Current.RecordingEnabled || _confirmingDesktop) return;
        int consentRevision = AppSettings.Current.RecordingDisableRevision;
        _confirmingDesktop = true;
        try
        {
            if (ModalDialog.Show(Application.Current.MainWindow,
                $"Record everything visible on {selected.Label} until you stop manual capture? Other apps, messages and notifications on that display can appear in clips.",
                "Allow manual desktop capture?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes)
                _ = recorder.SetManualDesktopAsync(selected.CaptureIdentity, consentRevision);
        }
        finally { _confirmingDesktop = false; }
    }

    private static MenuItem Item(string text, Action action, bool bold = false, bool enabled = true)
    {
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        if (AppProfile.IsPersonal) GameCatalogRuntime.Discovery.Dispose();
        _updateTimer?.Stop();
        Recorder.Instance.ReplaySaved -= OnReplaySaved;
        Recorder.Instance.SaveFailed -= OnSaveFailed;
        Recorder.Instance.StateChanged -= OnRecorderState;
        _clipOverlay.Dispose();
        if (_window == null) return;
        SuspendHotkey();
        var data = NewIconData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        _window.Dispose();
        _window = null;
    }

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_INFO = 0x1, NIIF_NOSOUND = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATAW data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
}
