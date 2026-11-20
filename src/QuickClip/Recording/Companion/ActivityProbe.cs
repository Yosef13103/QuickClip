using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace QuickClip.Recording.Companion;

internal sealed record ProbeResult(bool Complete, int Processes, int MicrophoneOwners,
    int UdpOwners, int MatchingOwners, int? Windows, string Error, long ProbeMs)
{
    internal WindowOwner[] Targets { get; init; } = [];
}

internal static class ActivityProbe
{
    private static readonly string[] Names = ["Discord", "DiscordPTB", "DiscordCanary"];
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedUdpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    // Install-path recognition only, not publisher attestation or permission.
    private static Dictionary<uint, long> Processes(string name)
    {
        using var self = Process.GetCurrentProcess();
        var found = new Dictionary<uint, long>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Names.Contains(name, StringComparer.Ordinal)) throw new ArgumentException("Unsupported Discord edition");
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var p in processes)
                {
                    if (p.SessionId != self.SessionId) continue;
                    long started = ProcessScan.StartTime((uint)p.Id);
                    if (started == 0) throw new InvalidOperationException("Process identity unavailable");
                    string path = p.MainModule?.FileName ?? throw new InvalidOperationException("Process identity unavailable");
                    string root = Path.Combine(local, name) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                        !Regex.IsMatch(path[root.Length..], @"^app-[0-9][0-9.a-zA-Z-]*\\Discord(?:PTB|Canary)?\.exe$", RegexOptions.IgnoreCase) ||
                        !string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Unrecognized Discord installation");
                    if (p.HasExited || ProcessScan.StartTime((uint)p.Id) != started)
                        throw new InvalidOperationException("Process changed");
                    found.Add((uint)p.Id, started);
                }
            }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        return found;
    }

    private static HashSet<uint> MicrophoneOwners(HashSet<uint> ids)
    {
        var active = new HashSet<uint>();
        var refs = new List<object>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumerator(); refs.Add(en);
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(1, 1, out var devices)); refs.Add(devices);
            devices.GetCount(out uint count);
            if (count == 0) throw new InvalidOperationException("No active capture endpoints");
            for (uint d = 0; d < count; d++)
            {
                var dev = devices.Item(d); refs.Add(dev);
                Guid iid = typeof(IAudioSessionManager2).GUID;
                dev.Activate(ref iid, 0x17, IntPtr.Zero, out object manager); refs.Add(manager);
                var sessions = ((IAudioSessionManager2)manager).GetSessionEnumerator(); refs.Add(sessions);
                for (int i = 0, n = sessions.GetCount(); i < n; i++)
                {
                    var ctl = sessions.GetSession(i); refs.Add(ctl);
                    var session = (IAudioSessionControl2)ctl;
                    int hr = session.GetProcessId(out uint pid);
                    Marshal.ThrowExceptionForHR(hr);
                    if (!ids.Contains(pid)) continue;
                    if (hr != 0) throw new InvalidOperationException("Shared audio session ownership");
                    Marshal.ThrowExceptionForHR(session.GetState(out int state));
                    if (state == 1) active.Add(pid);
                }
            }
        }
        finally
        {
            for (int i = refs.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(refs[i]);
        }
        return active;
    }

    private static void UdpOwners(int family, int rowSize, HashSet<uint> ids, HashSet<uint> owners)
    {
        int len = 0;
        uint initial = GetExtendedUdpTable(IntPtr.Zero, ref len, false, family, 1, 0);
        if (initial != 122 && initial != 0) throw new InvalidOperationException("UDP enumeration unavailable");
        for (int tries = 0; tries < 3; tries++)
        {
            if (len < 4 || len > 32 * 1024 * 1024) throw new InvalidOperationException("UDP table size");
            IntPtr p = Marshal.AllocHGlobal(len);
            try
            {
                int actual = len;
                uint rc = GetExtendedUdpTable(p, ref actual, false, family, 1, 0);
                if (rc == 122) { len = actual; continue; }
                if (rc != 0 || actual < 4 || actual > len) throw new InvalidOperationException("UDP table unavailable");
                int n = Marshal.ReadInt32(p);
                if (n < 0 || n > (actual - 4) / rowSize) throw new InvalidOperationException("UDP table bounds");
                for (int i = 0; i < n; i++)
                {
                    uint pid = unchecked((uint)Marshal.ReadInt32(p, 4 + i * rowSize + rowSize - 4));
                    if (ids.Contains(pid)) owners.Add(pid);
                }
                return;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        throw new InvalidOperationException("UDP table changed repeatedly");
    }

    private static WindowOwner[]? ReadWindows(Dictionary<uint, long> owners)
    {
        var windows = new List<WindowOwner>();
        bool valid = true;
        bool ok = EnumWindows((hwnd, _) =>
        {
            uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
            if (thread != 0 && owners.TryGetValue(pid, out long started) && IsWindowVisible(hwnd))
            {
                if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) != 0) valid = false;
                else if (cloaked == 0) windows.Add(new(hwnd, thread, pid, started));
            }
            return true;
        }, IntPtr.Zero);
        return ok && valid && windows.All(GameDetector.IsCurrent) ? windows.ToArray() : null;
    }

    public static ProbeResult Read(string edition = "Discord", bool requireCallEvidence = true)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var before = Processes(edition);
            var ids = before.Keys.ToHashSet();
            if (ids.Count == 0)
                return new(true, 0, 0, 0, 0, 0, "", watch.ElapsedMilliseconds);
            var mic = requireCallEvidence ? MicrophoneOwners(ids) : new HashSet<uint>();
            var udp = new HashSet<uint>();
            if (requireCallEvidence) { UdpOwners(2, 12, ids, udp); UdpOwners(23, 28, ids, udp); }
            var windows = ReadWindows(before);
            var after = Processes(edition);
            if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out long started) || started != pair.Value))
                return new(false, 0, 0, 0, 0, null, "Discord processes changed during sampling", watch.ElapsedMilliseconds);
            return new(true, ids.Count, mic.Count, udp.Count, mic.Count(udp.Contains), windows?.Length, "", watch.ElapsedMilliseconds) { Targets = windows ?? [] };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never include file paths, endpoint IDs, window titles or network addresses.
            return new(false, 0, 0, 0, 0, null, "Probe incomplete (" + ex.GetType().Name + ")", watch.ElapsedMilliseconds);
        }
    }
}
