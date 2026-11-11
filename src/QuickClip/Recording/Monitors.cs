using System.Runtime.InteropServices;

namespace QuickClip.Recording;

public sealed record MonitorInfo(string DeviceName, string FriendlyName, int Left, int Top, int Width, int Height)
{
    public bool IsPrimary => Left == 0 && Top == 0;

    public string Label
    {
        get
        {
            string name = string.IsNullOrWhiteSpace(FriendlyName) ? DeviceName.Replace(@"\\.\", "") : FriendlyName;
            return $"{name} — {Width}×{Height}{(IsPrimary ? " (main)" : "")}";
        }
    }
}

/// <summary>Monitors the engine can capture, with the names Windows shows in display settings.</summary>
internal static class Monitors
{
    internal sealed record DisplayMode(string Name, int SourceWidth, int SourceHeight, int SignalWidth, int SignalHeight, double Refresh);
    internal static string Diagnostics()
    {
        var modes = ReadModes();
        return CaptureEngine.DisplayDiagnostics() + string.Join("\n", modes.Select(p =>
            $"{p.Key} {p.Value.Name} | QueryDisplayConfig source {p.Value.SourceWidth}×{p.Value.SourceHeight}; signal {p.Value.SignalWidth}×{p.Value.SignalHeight} @ {p.Value.Refresh:0.##} Hz"));
    }

    internal static (int Width, int Height) PhysicalSize(int nativeWidth, int nativeHeight, int sourceWidth, int sourceHeight) =>
        sourceWidth > 0 && sourceHeight > 0 ? (sourceWidth, sourceHeight) : (nativeWidth, nativeHeight);

    public static IReadOnlyList<MonitorInfo> List()
    {
        if (!CaptureEngine.IsAvailable) return [];
        CaptureEngine.Initialize();
        var names = ReadModes();
        return CaptureEngine.EnumOutputs()
            .Select(o =>
            {
                var mode = names.GetValueOrDefault(o.DeviceName);
                var size = PhysicalSize(o.Right - o.Left, o.Bottom - o.Top, mode?.SourceWidth ?? 0, mode?.SourceHeight ?? 0);
                return new MonitorInfo(o.DeviceName, mode?.Name ?? "", o.Left, o.Top, size.Width, size.Height);
            })
            .OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left)
            .ToList();
    }

    /// <summary>GDI device name (\\.\DISPLAY1) → monitor's friendly name, via the display configuration API.</summary>
    private static Dictionary<string, DisplayMode> ReadModes()
    {
        var result = new Dictionary<string, DisplayMode>(StringComparer.OrdinalIgnoreCase);
        try
        {
            DISPLAYCONFIG_PATH_INFO[] paths = [];
            DISPLAYCONFIG_MODE_INFO[] modes = [];
            uint pathCount = 0, modeCount = 0;
            int status = 122;
            for (int retry = 0; retry < 3 && status == 122; retry++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out pathCount, out modeCount) != 0) return result;
                paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
                modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
                status = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
            }
            if (status != 0) return result;
            foreach (var path in paths.Take((int)pathCount))
            {
                var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 1, // GET_SOURCE_NAME
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                        adapterId = path.sourceInfo.adapterId,
                        id = path.sourceInfo.id,
                    },
                };
                var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                    {
                        type = 2, // GET_TARGET_NAME
                        size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id,
                    },
                };
                if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;
                DisplayConfigGetDeviceInfo(ref target);
                var sm = path.sourceInfo.modeInfoIdx < modeCount ? modes[path.sourceInfo.modeInfoIdx] : default;
                var tm = path.targetInfo.modeInfoIdx < modeCount ? modes[path.targetInfo.modeInfoIdx] : default;
                result[source.viewGdiDeviceName] = new(target.monitorFriendlyDeviceName ?? "",
                    sm.infoType == 1 ? (int)sm.sourceWidth : 0, sm.infoType == 1 ? (int)sm.sourceHeight : 0,
                    tm.infoType == 2 ? (int)tm.signalWidth : 0, tm.infoType == 2 ? (int)tm.signalHeight : 0,
                    path.targetInfo.refreshDenominator == 0 ? 0 : (double)path.targetInfo.refreshNumerator / path.targetInfo.refreshDenominator);
            }
        }
        catch (Exception ex)
        {
            Log.Write("Reading monitor names failed: " + ex.Message);
        }
        return result;
    }

    private const uint QDC_ONLY_ACTIVE_PATHS = 2;

    [StructLayout(LayoutKind.Sequential)] private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshNumerator; public uint refreshDenominator; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    // 48-byte union after a 16-byte header: source mode and target VIDEO_SIGNAL_INFO overlap.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(16)] public uint sourceWidth;
        [FieldOffset(20)] public uint sourceHeight;
        [FieldOffset(40)] public uint signalWidth;
        [FieldOffset(44)] public uint signalHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME info);
}
