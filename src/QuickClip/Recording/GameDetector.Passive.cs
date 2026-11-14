using System.Diagnostics;
using System.Runtime.InteropServices;
using QuickClip.Discovery;

namespace QuickClip.Recording;

internal readonly record struct PassiveGameWindow(WindowOwner Owner, string Executable, long ClientArea);

internal sealed partial class GameDetector
{
    private static readonly IReadOnlySet<string> NoKnownGames = new HashSet<string>();
    private const int MaxWindows = 512;
    private const int MaxProcesses = 48;
    private static readonly TimeSpan WindowBudget = TimeSpan.FromMilliseconds(100);

    /// <summary>Call from an armed recorder's existing observation tick. This method never starts capture.</summary>
    internal GameCaptureObservation? ObserveEligibleGame(GameCaptureObservation? retained,
        Func<GameCaptureObservation?, GameCaptureObservation?, bool> preferForeground) =>
        AppProfile.IsPersonal
            ? ResolveEligibleGame(retained, DetectForeground(), IsObservableOwner, ReadPassiveWindows,
                GameCatalogRuntime.Match, preferForeground)
            : null;

    internal static GameCaptureObservation? ResolveEligibleGame(GameCaptureObservation? retained,
        GameCaptureObservation? foreground, Func<WindowOwner, bool> isCurrent,
        Func<IReadOnlyList<PassiveGameWindow>> windows, Func<string, bool, CatalogMatch> match,
        Func<GameCaptureObservation?, GameCaptureObservation?, bool>? preferForeground = null)
    {
        GameCaptureObservation? verifiedForeground = null;
        if (foreground?.VerifiedTarget != null)
        {
            var updated = Observe(foreground.Owner, foreground.Executable, match(foreground.Executable, true),
                false, NoKnownGames);
            if (updated.VerifiedTarget != null && isCurrent(updated.Owner)) verifiedForeground = updated;
        }
        if (preferForeground == null && verifiedForeground != null) return verifiedForeground;

        var live = verifiedForeground != null && verifiedForeground.Owner == retained?.Owner ? verifiedForeground :
            SelectVerifiedObservation(retained, null, isCurrent, match);
        bool replace = preferForeground?.Invoke(live, verifiedForeground) ?? true;
        if (verifiedForeground != null && (live == null || replace)) return verifiedForeground;
        if (live != null) return live;

        GameCaptureObservation? best = null;
        long bestArea = 0;
        foreach (var candidate in windows())
        {
            if (candidate.ClientArea <= 0 || candidate.Owner == foreground?.Owner) continue;
            var catalog = match(candidate.Executable, false);
            if (catalog.Kind is not (CatalogMatchKind.Game or CatalogMatchKind.Candidate)) continue;
            var observation = Observe(candidate.Owner, candidate.Executable, catalog, false, NoKnownGames)
                with { IsForeground = false };
            if (observation.VerifiedTarget == null || !isCurrent(candidate.Owner)) continue;
            if (best != null && observation.Match.Entry?.Id != best.Match.Entry?.Id) return null;
            if (candidate.ClientArea <= bestArea) continue;
            bestArea = candidate.ClientArea;
            best = observation;
        }
        return best;
    }

    internal static bool IsForegroundOwner(WindowOwner owner) => GetForegroundWindow() == owner.Handle;

    internal static bool IsObservableOwner(WindowOwner owner) =>
        VisibleClientArea(owner.Handle) > 0 && IsCurrent(owner);

    private static IReadOnlyList<PassiveGameWindow> ReadPassiveWindows()
    {
        var windows = new List<PassiveGameWindow>();
        var processes = new Dictionary<uint, (long Started, string? Path)>();
        int visited = 0;
        long startedAt = Stopwatch.GetTimestamp();
        bool complete = EnumWindows((hwnd, _) =>
        {
            if (++visited > MaxWindows || Stopwatch.GetElapsedTime(startedAt) >= WindowBudget) return false;
            long area = VisibleClientArea(hwnd);
            if (area == 0) return true;
            uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
            if (thread == 0 || pid == 0 || pid == Environment.ProcessId) return true;
            if (!processes.TryGetValue(pid, out var process))
            {
                if (processes.Count == MaxProcesses) return false;
                long started = ProcessScan.StartTime(pid);
                process = (started, started == 0 ? null : ProcessScan.ExePath(pid));
                processes.Add(pid, process);
            }
            if (process.Path != null)
                windows.Add(new(new(hwnd, thread, pid, process.Started), process.Path, area));
            return true;
        }, IntPtr.Zero);
        return complete ? windows : [];
    }

    private static long VisibleClientArea(IntPtr hwnd)
    {
        const int ExStyle = -20, ToolWindow = 0x80;
        const uint Owner = 4, Cloaked = 14;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindow(hwnd, Owner) != IntPtr.Zero ||
            (GetWindowLong(hwnd, ExStyle) & ToolWindow) != 0 ||
            DwmGetWindowAttribute(hwnd, Cloaked, out int cloaked, sizeof(int)) != 0 || cloaked != 0 ||
            !GetClientRect(hwnd, out var rect)) return 0;
        long width = (long)rect.Right - rect.Left, height = (long)rect.Bottom - rect.Top;
        return width > 0 && height > 0 ? width * height : 0;
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool WindowVisitor(IntPtr hwnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visit, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, int size);
}
