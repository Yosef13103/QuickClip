using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuickClip.Discovery;

namespace QuickClip.Recording;

internal readonly record struct WindowOwner(IntPtr Handle, uint ThreadId, uint ProcessId, long Started);
internal sealed record GameWindowTarget(WindowOwner Owner, string Game, bool Recognized);
internal sealed record GameCaptureObservation(WindowOwner Owner, string Executable, CatalogMatch Match,
    GameWindowTarget? Target, bool IsForeground)
{
    /// <summary>Eligible exact catalog identity for the armed recorder. The legacy target can be heuristic.</summary>
    public GameWindowTarget? VerifiedTarget => !SkipAutomaticCapture && Target is { Recognized: true } target &&
        (Match.Kind == CatalogMatchKind.Game || Match is { Kind: CatalogMatchKind.Candidate, Entry.Source: CatalogSource.Steam })
            ? target : null;

    public bool SkipAutomaticCapture => Match.Kind is CatalogMatchKind.Ignored or CatalogMatchKind.Inactive
        or CatalogMatchKind.Ambiguous or CatalogMatchKind.Launcher or CatalogMatchKind.Application
        || Match.Kind == CatalogMatchKind.Candidate && Target == null;
    public GameRecordingPolicy ResolvePolicy(GameProfileDocument profiles, GameRecordingPolicy defaults) =>
        profiles.Resolve(Match.Entry?.Rules.ProfileId, defaults);
}

/// <summary>Works out which game a clip belongs to (null = not a game, filed under "Desktop").</summary>
internal sealed partial class GameDetector
{
    // Apps that are never games, even when fullscreen.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "QuickClip", "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "iexplore",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "mpv", "PotPlayerMini64", "Microsoft.Media.Player", "wmplayer", "Spotify",
        "obs64", "obs32", "Code", "devenv", "rider64", "idea64", "notepad", "notepad++", "WindowsTerminal", "cmd", "powershell", "pwsh",
        "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "RiotClientServices", "RiotClientUx", "EADesktop", "EALauncher",
        "GalaxyClient", "UbisoftConnect", "upc", "Overwolf", "Medal", "XboxPcApp", "GameBar", "ApplicationFrameHost", "SearchHost",
        "ShellExperienceHost", "StartMenuExperienceHost", "TextInputHost", "LockApp", "Photos", "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK",
        "Resolve", "CapCut", "Ableton Live 12 Suite", "blender", "Unity", "UnrealEditor", "AdobePremierePro", "Photoshop",
    };

    // Store/launcher install roots: the folder right after one of these is the game's name.
    private static readonly string[] GameRoots =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Galaxy\Games\", @"\GOG Games\", @"\EA Games\",
        @"\Ubisoft Game Launcher\games\", @"\XboxGames\", @"\Battle.net\Games\", @"\Rockstar Games\", @"\Games\",
    ];

    private static readonly string[] MeaninglessProductNames =
        ["Unreal Engine", "UnrealGame", "UE4Game", "BootstrapPackagedGame", "Unity", "Microsoft® Windows® Operating System"];

    private HashSet<string> _knownGames = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _knownGamesLoaded;
    private string? _lastGame;
    private DateTime _lastGameSeen;

    static GameDetector()
    {
        foreach (var app in ChatApps.Executables.Concat(MusicApps.Executables)) NotGames.Add(app);
    }

    /// <summary>The game window in the foreground at the last <see cref="Poll"/>.</summary>
    public GameWindowTarget? ForegroundGame { get; private set; }
    public GameCaptureObservation? ForegroundObservation { get; private set; }
    public GameCaptureObservation? ActiveObservation { get; private set; }

    /// <summary>Called every few seconds: remembers the game in the foreground, if any.</summary>
    public void Poll()
    {
        ForegroundObservation = DetectForeground();
        ActiveObservation = SelectObservation(ActiveObservation, ForegroundObservation, IsCurrent);
        ForegroundGame = ForegroundObservation?.Target;
        if (ForegroundObservation is { Match.Kind: not CatalogMatchKind.Unknown } observed
            && ForegroundGame != null)
        {
            // The old recorder can start WGC for any target delivered here. Preserve its existing
            // eligibility until the capture adapter enforces an explicit, borderless source policy.
            if (Classify(observed.Executable, IsFullscreen(observed.Owner.Handle), KnownGames()) == null)
                ForegroundGame = null;
            else
                ForegroundGame = ForegroundGame with
                {
                    Recognized = KnownGames().Contains(observed.Executable) || GameFolderName(observed.Executable) != null,
                };
        }
        if ((AppProfile.IsPersonal ? ActiveObservation?.Target : ForegroundGame) is not { } target)
        {
            if (ForegroundObservation?.SkipAutomaticCapture == true) _lastGame = null;
            return;
        }
        _lastGame = target.Game;
        _lastGameSeen = DateTime.UtcNow;
    }

    /// <summary>The game a clip ending now belongs to: the foreground game, or the last one played during the clip.</summary>
    public string? GameForClip(int clipSeconds)
    {
        var observed = DetectForeground();
        if (observed?.SkipAutomaticCapture == true) return null;
        var game = observed?.Target?.Game;
        if (game != null) return game;
        return _lastGame != null && DateTime.UtcNow - _lastGameSeen <= TimeSpan.FromSeconds(clipSeconds + 5) ? _lastGame : null;
    }

    internal string? VerifiedGameContext() =>
        VerifiedGameContext(ActiveObservation, DetectForeground(), IsCurrent);

    internal static GameCaptureObservation? SelectVerifiedObservation(GameCaptureObservation? retained,
        GameCaptureObservation? foreground, Func<WindowOwner, bool> isCurrent, Func<string, bool, CatalogMatch>? match = null)
    {
        var selected = SelectObservation(retained?.VerifiedTarget != null ? retained : null,
            foreground?.VerifiedTarget != null ? foreground : null, isCurrent, match);
        return selected?.VerifiedTarget != null ? selected : null;
    }

    internal static string? VerifiedGameContext(GameCaptureObservation? current,
        GameCaptureObservation? foreground, Func<WindowOwner, bool> isCurrent,
        Func<string, bool, CatalogMatch>? match = null) =>
        SelectObservation(current, foreground, isCurrent, match)?.Target is { Recognized: true } target
            ? target.Game : null;

    private GameCaptureObservation? DetectForeground()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            uint threadId = GetWindowThreadProcessId(hwnd, out uint pid);
            if (threadId == 0 || pid == 0 || pid == Environment.ProcessId) return null;
            long started = ProcessScan.StartTime(pid);
            if (started == 0) return null;
            string? path = ProcessScan.ExePath(pid);
            if (path == null) return null;
            var known = KnownGames();
            var owner = new WindowOwner(hwnd, threadId, pid, started);
            bool fullscreen = IsFullscreen(hwnd);
            var observed = AppProfile.IsPersonal
                ? Observe(owner, path, GameCatalogRuntime.Match(path, true), fullscreen, known, () => WindowTitle(hwnd))
                : new GameCaptureObservation(owner, path, new(CatalogMatchKind.Unknown),
                    Classify(path, fullscreen, known, () => WindowTitle(hwnd)) is { } name
                        ? new(owner, name, known.Contains(path) || GameFolderName(path) != null) : null, true);
            return IsCurrent(owner) ? observed : null;
        }
        catch (Exception ex)
        {
            Log.Write("Game detection failed: " + ex.Message);
            return null;
        }
    }

    internal static GameCaptureObservation Observe(WindowOwner owner, string path, CatalogMatch match,
        bool fullscreen, IReadOnlySet<string> known, Func<string>? title = null)
    {
        string executable = Path.GetFileNameWithoutExtension(path);
        if (CatalogPaths.IsDiscordProcess(path)) match = new(CatalogMatchKind.Companion);
        else if (NotGames.Contains(executable)) match = new(CatalogMatchKind.Application, match.Entry);
        else if (executable.ToLowerInvariant() is "launcher" or "gamelauncher" or "updater" or "update")
            match = new(CatalogMatchKind.Launcher, match.Entry);

        bool recognized = match.Kind == CatalogMatchKind.Game
            || match.Kind == CatalogMatchKind.Candidate && (known.Contains(path) || fullscreen ||
                match.Entry?.Source == CatalogSource.Steam);
        string? name = recognized ? match.Entry?.DisplayName : null;
        if (match.Kind == CatalogMatchKind.Unknown)
        {
            name = Classify(path, fullscreen, known, title);
            recognized = known.Contains(path) || GameFolderName(path) != null;
        }
        return new(owner, path, match, name == null ? null : new(owner, SanitizeFolderName(name), recognized), true);
    }

    internal static GameCaptureObservation? SelectObservation(GameCaptureObservation? current,
        GameCaptureObservation? foreground, Func<WindowOwner, bool> isCurrent,
        Func<string, bool, CatalogMatch>? match = null)
    {
        match ??= GameCatalogRuntime.Match;
        if (foreground?.Target is { Recognized: true } target && isCurrent(target.Owner)) return foreground;
        if (current?.Target != null && isCurrent(current.Owner))
        {
            var latest = match(current.Executable, foreground?.Owner == current.Owner);
            if ((latest.Kind is CatalogMatchKind.Unknown or CatalogMatchKind.Candidate or CatalogMatchKind.Game)
                && latest.Entry?.Id == current.Match.Entry?.Id)
                return current with
                {
                    Match = latest,
                    Target = latest.Entry is { } entry
                        ? current.Target with { Game = SanitizeFolderName(entry.DisplayName) }
                        : current.Target,
                    IsForeground = foreground?.Owner == current.Owner,
                };
        }
        return foreground?.Target != null && isCurrent(foreground.Owner) ? foreground : null;
    }

    /// <summary>Game name for an executable, or null if it doesn't look like a game.</summary>
    public static string? Classify(string exePath, bool fullscreen, IReadOnlySet<string> knownGames, Func<string>? windowTitle = null)
    {
        string exe = Path.GetFileNameWithoutExtension(exePath);
        if (NotGames.Contains(exe) || (AppProfile.IsPersonal ? CatalogPaths.IsDiscordProcess(exePath) : CatalogPaths.IsDiscordUpdater(exePath))) return null;
        string? folderName = GameFolderName(exePath);
        bool known = knownGames.Contains(exePath);
        if (!known && folderName == null && !fullscreen) return null;

        string? name = folderName;
        if (name == null)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(exePath);
                foreach (var candidate in new[] { info.ProductName, info.FileDescription })
                    if (!string.IsNullOrWhiteSpace(candidate) && !MeaninglessProductNames.Contains(candidate.Trim(), StringComparer.OrdinalIgnoreCase))
                    {
                        name = candidate.Trim();
                        break;
                    }
            }
            catch { }
        }
        if (string.IsNullOrWhiteSpace(name)) name = windowTitle?.Invoke();
        if (string.IsNullOrWhiteSpace(name)) name = exe;
        return SanitizeFolderName(name!);
    }

    private static string? GameFolderName(string path)
    {
        foreach (var root in GameRoots)
        {
            int i = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            int start = i + root.Length;
            int end = path.IndexOf('\\', start);
            if (end > start) return path[start..end];
        }
        return null;
    }

    public static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c < 32 ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (clean.Length > 60) clean = clean[..60].Trim();
        return clean.Length == 0 ? "Game" : clean;
    }

    /// <summary>Executables Windows itself has recognised as games (Game Bar's list).</summary>
    private IReadOnlySet<string> KnownGames()
    {
        if (DateTime.UtcNow - _knownGamesLoaded < TimeSpan.FromMinutes(10)) return _knownGames;
        _knownGamesLoaded = DateTime.UtcNow;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (children != null)
                foreach (var name in children.GetSubKeyNames())
                {
                    using var child = children.OpenSubKey(name);
                    if (child?.GetValue("MatchedExeFullPath") is string exe && exe.Length > 0) set.Add(exe);
                }
        }
        catch { }
        return _knownGames = set;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        const int GWL_STYLE = -16, WS_CAPTION = 0x00C00000;
        if ((GetWindowLong(hwnd, GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false; // a maximised normal window
        if (!GetWindowRect(hwnd, out var rect)) return false;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref info)) return false;
        var m = info.rcMonitor;
        return rect.Left <= m.Left && rect.Top <= m.Top && rect.Right >= m.Right && rect.Bottom >= m.Bottom;
    }


    internal static string WindowTitle(IntPtr hwnd, int max = 256)
    {
        var buffer = new char[max];
        int n = GetWindowText(hwnd, buffer, buffer.Length);
        return new string(buffer, 0, Math.Max(0, n));
    }

    /// <summary>A foreground candidate must still belong to the observed process. Fullscreen alone cannot displace a live game.</summary>
    public static GameWindowTarget? SelectTarget(GameWindowTarget? current, GameWindowTarget? foreground, Func<WindowOwner, bool> isCurrent)
    {
        if (foreground != null && foreground.Recognized && isCurrent(foreground.Owner)) return foreground;
        if (current != null && isCurrent(current.Owner)) return current;
        return foreground != null && isCurrent(foreground.Owner) ? foreground : null;
    }

    public static bool IsCurrent(WindowOwner owner) =>
        GetWindowThreadProcessId(owner.Handle, out uint pid) == owner.ThreadId &&
        pid == owner.ProcessId && ProcessScan.StartTime(pid) == owner.Started;

    /// <summary>The size of a window's inside (without its frame) in real pixels, as capture sees it; (0, 0) while minimized.</summary>
    public static (int Width, int Height) WindowSize(IntPtr hwnd)
    {
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: real pixels whatever this app's DPI mode
        try
        {
            return !IsIconic(hwnd) && GetClientRect(hwnd, out var r) ? (r.Right - r.Left, r.Bottom - r.Top) : (0, 0);
        }
        finally
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, [Out] char[] text, int max);
}
