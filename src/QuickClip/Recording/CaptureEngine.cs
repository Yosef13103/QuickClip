using System.Runtime.InteropServices;

namespace QuickClip.Recording;

/// <summary>P/Invoke surface of QuickClipCapture.dll (src/QuickClip.Capture). Struct layouts mirror Engine.h.</summary>
internal static partial class CaptureEngine
{
    internal enum MonitorBackend { Automatic, DesktopDuplication, GraphicsCapture, MonitorOnlyDuplication, MonitorOnlyGraphics, DiscordDwmMirror }
    internal enum NvencSpeed { BalancedP4, FasterP3, FastestP2 }
    [StructLayout(LayoutKind.Sequential)]
    internal struct VideoOptions
    {
        public MonitorBackend Backend;
        public NvencSpeed Preset;
        public int Diagnostics;
    }
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GameWindow(IntPtr Handle, uint Thread, uint Process, long Started);

    private const string Dll = "QuickClipCapture.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public IntPtr MonitorDevice;
        public int Fps;
        public int OutWidth;
        public int OutHeight;
        public int AspectWidth, AspectHeight;
        public IntPtr Encoder;
        public int VideoKbps;
        public int AudioKbps;
        public int BufferSeconds;
        public int CaptureCursor;
        public int SplitChat;
        public int SplitMusic;
        public int MicCount;
        public IntPtr MicDeviceIds;
        public IntPtr MicTitles;
        public int MicSeparate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stats
    {
        public int Running;
        public int Width;
        public int Height;
        public double Fps;
        public long BufferBytes;
        public double BufferSeconds;
        public long DroppedFrames;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Output
    {
        public int Adapter;
        public int OutputIndex;
        public int Left, Top, Right, Bottom;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string AdapterName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct AudioDevice
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public int IsDefault;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EndpointConfig
    {
        public int Flow;
        public IntPtr DeviceId;
        public IntPtr Title;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LogCallback(DiagnosticCategory category, DiagnosticSeverity severity, IntPtr message, int code);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate void EndpointCallback(int flow, [MarshalAs(UnmanagedType.LPWStr)] string id,
        [MarshalAs(UnmanagedType.LPWStr)] string name, int isDefault);

    private static class DiagnosticCallback
    {
        internal static readonly LogCallback Handle = (category, severity, message, code) =>
        {
            if (!Log.Enabled) return;
            if (Marshal.PtrToStringUTF8(message) is { } summary)
                Log.Native(category, severity, summary, code == 0 ? null : code);
        };
    }
    private static readonly object InitializationGate = new();
    private static bool _initialized;
    private static bool _diagnosticLogging;

    public static bool IsAvailable => File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));

    /// <summary>Points the loader at the bundled FFmpeg DLLs the engine links against. Call before any other method.</summary>
    public static void Initialize()
    {
        lock (InitializationGate)
        {
            if (_initialized) return;
            SetDllDirectory(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
            _diagnosticLogging = Log.Enabled;
            qc_set_diagnostic_log(_diagnosticLogging ? DiagnosticCallback.Handle : null);
            Volatile.Write(ref _initialized, true);
        }
    }

    internal static void UpdateDiagnosticLogging()
    {
        lock (InitializationGate)
        {
            bool enabled = Log.Enabled;
            if (!_initialized || _diagnosticLogging == enabled) return;
            qc_set_diagnostic_log(enabled ? DiagnosticCallback.Handle : null);
            _diagnosticLogging = enabled;
        }
    }

    public static IReadOnlyList<Output> EnumOutputs()
    {
        var buffer = new Output[16];
        int n = qc_enum_outputs(buffer, buffer.Length);
        return buffer.Take(Math.Min(n, buffer.Length)).ToList();
    }

    public static IReadOnlyList<AudioDevice> EnumMicrophones()
    {
        var buffer = new AudioDevice[32];
        int n = qc_enum_microphones(buffer, buffer.Length);
        return buffer.Take(Math.Min(n, buffer.Length)).ToList();
    }

    public static IReadOnlyList<AudioEndpoint> EnumAudioEndpoints()
    {
        var endpoints = new List<AudioEndpoint>();
        qc_enum_audio_endpoints((flow, id, name, isDefault) => endpoints.Add(new AudioEndpoint((EndpointFlow)flow, id, name, isDefault != 0)));
        return endpoints;
    }

    /// <param name="aspect">Record only the centered part of the monitor with this shape ("16:9"), or "" for all of it.
    /// The engine works the area out itself, again after a display change.</param>
    /// <param name="outWidth">Size to scale the area to, or 0 for its native size.</param>
    /// <param name="mics">Microphones to record (see <see cref="Microphones.Plan"/>).</param>
    /// <param name="separateMics">Each microphone on its own track, named by its title, instead of all on one Mic track.</param>
    public static string? Start(string monitorDevice, string aspect, int fps, int outWidth, int outHeight, string encoder, int videoKbps,
        int audioKbps, int bufferSeconds, bool cursor, bool splitChat, bool splitMusic, IReadOnlyList<Microphones.Input> mics,
        bool separateMics, IReadOnlyList<EndpointTrack>? endpointTracks = null, VideoOptions video = default, string? displayIdentity = null, WindowOwner? game = null,
        IReadOnlyList<WindowOwner>? companion = null)
    {
        var strings = new List<IntPtr>();
        IntPtr Uni(string? s) { var p = s == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s); strings.Add(p); return p; }
        IntPtr UniArray(IEnumerable<string> items)
        {
            var pointers = items.Select(Uni).ToList();
            var array = Marshal.AllocHGlobal(IntPtr.Size * Math.Max(1, pointers.Count));
            strings.Add(array);
            for (int i = 0; i < pointers.Count; i++) Marshal.WriteIntPtr(array, i * IntPtr.Size, pointers[i]);
            return array;
        }
        try
        {
            var config = new Config
            {
                MonitorDevice = Uni(monitorDevice),
                Fps = fps,
                OutWidth = outWidth,
                OutHeight = outHeight,
                AspectWidth = RecordingPresets.ParseAspect(aspect).Width,
                AspectHeight = RecordingPresets.ParseAspect(aspect).Height,
                Encoder = Marshal.StringToHGlobalAnsi(encoder),
                VideoKbps = videoKbps,
                AudioKbps = audioKbps,
                BufferSeconds = bufferSeconds,
                CaptureCursor = cursor ? 1 : 0,
                SplitChat = splitChat ? 1 : 0,
                SplitMusic = splitMusic ? 1 : 0,
                MicCount = mics.Count,
                MicDeviceIds = UniArray(mics.Select(m => m.DeviceId)),
                MicTitles = UniArray(mics.Select(m => m.Title)),
                MicSeparate = separateMics ? 1 : 0,
            };
            strings.Add(config.Encoder);
            var error = new char[1024];
            var tracks = (endpointTracks ?? []).Select(t => new EndpointConfig
                {
                    Flow = (int)t.Flow,
                    DeviceId = Uni(t.DeviceId),
                    Title = Uni(t.Label),
                }).ToArray();
            int count = endpointTracks == null ? -1 : tracks.Length;
            if (video.Backend == MonitorBackend.DiscordDwmMirror && companion is not { Count: >= 1 and <= 4 })
                return "Experimental Discord mirroring requires authorized companion windows.";
            if (companion != null &&
                (companion.Count is < 1 or > 4 || game != null || displayIdentity != null ||
                 companion.Any(owner => !GameDetector.IsCurrent(owner)) ||
                 companion.Select(owner => owner.Handle).Distinct().Count() != companion.Count))
                return "The authorized companion window group is unavailable.";
            int started = companion is { Count: > 0 }
                ? qc_start_companion(ref config, tracks, count, ref video,
                    companion.Select(owner => new GameWindow(owner.Handle, owner.ThreadId,
                        owner.ProcessId, owner.Started)).ToArray(), companion.Count, error, error.Length)
                : game is { } owner
                ? qc_start_with_source(ref config, tracks, count, ref video, displayIdentity,
                    new GameWindow(owner.Handle, owner.ThreadId, owner.ProcessId, owner.Started), error, error.Length)
                : qc_start_with_display(ref config, tracks, count, ref video, displayIdentity, error, error.Length);
            return started != 0 ? null : new string(error).TrimEnd('\0');
        }
        finally
        {
            foreach (var p in strings) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    internal static bool UpdateCompanionWindows(IReadOnlyList<WindowOwner> owners) =>
        owners.Count is >= 1 and <= 4 &&
        owners.Select(owner => owner.Handle).Distinct().Count() == owners.Count &&
        owners.All(GameDetector.IsCurrent) &&
        qc_update_companion(owners.Select(owner => new GameWindow(owner.Handle,
            owner.ThreadId, owner.ProcessId, owner.Started)).ToArray(), owners.Count) != 0;

    public static void Stop() => qc_stop();

    // Only the verified game recorder may call this. Native independently
    // rechecks live HWND/PID/process creation time and physical display.
    internal static bool SealGameEpochAndStop(Guid session, long epoch,
        ReplayEpochGameProof proof)
    {
        if (session == Guid.Empty || epoch <= 0 || proof.DisableRevision < 0 ||
            proof.PermissionRevision is < 0 or > uint.MaxValue ||
            !ReplayEpochContinuity.IsValidProof(proof))
        {
            Stop();
            return false;
        }

        ulong sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 0);
        if (sessionToken == 0) sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 8);
        if (sessionToken == 0) { Stop(); return false; }
        ulong grant = ((ulong)(uint)proof.DisableRevision << 32) | (uint)proof.PermissionRevision;
        string scope = EpochScope(proof);
        var error = new char[1024];
        return qc_seal_game_epoch_and_stop(sessionToken, (ulong)epoch, grant, scope,
            error, error.Length) != 0;
    }

    internal static string EpochScope(ReplayEpochGameProof proof) =>
        proof.GameId + "|" + proof.VerifiedExecutable;

    [StructLayout(LayoutKind.Sequential)]
    internal struct EpochDescriptor
    {
        public ulong EpochId;
        public long FirstUs, LastUs;
        public int Width, Height;
        public long CompressedBytes;
    }

    internal static EpochDescriptor[] ListGameEpochs(Guid session,
        ReplayEpochGameProof proof, out long activeGraphStartUs)
    {
        activeGraphStartUs = 0;
        if (session == Guid.Empty || !ReplayEpochContinuity.IsValidProof(proof) ||
            proof.PermissionRevision is < 0 or > uint.MaxValue) return [];
        ulong sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 0);
        if (sessionToken == 0) sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 8);
        ulong grant = ((ulong)(uint)proof.DisableRevision << 32) | (uint)proof.PermissionRevision;
        var epochs = new EpochDescriptor[32];
        int count = qc_list_game_epochs(sessionToken, grant, EpochScope(proof),
            epochs, epochs.Length, out activeGraphStartUs);
        return count > 0 ? epochs.Take(Math.Min(count, epochs.Length)).ToArray() : [];
    }

    internal static string? WriteGameEpoch(Guid session, ReplayEpochGameProof proof,
        ulong epochId, string path)
    {
        if (session == Guid.Empty || !ReplayEpochContinuity.IsValidProof(proof) ||
            proof.PermissionRevision is < 0 or > uint.MaxValue)
            return "Archived game authorization is invalid.";
        ulong sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 0);
        if (sessionToken == 0) sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 8);
        ulong grant = ((ulong)(uint)proof.DisableRevision << 32) | (uint)proof.PermissionRevision;
        var error = new char[1024];
        return qc_write_game_epoch(sessionToken, grant, EpochScope(proof),
            epochId, path, error, error.Length) != 0
                ? null : new string(error).TrimEnd('\0');
    }

    internal static void DiscardEpochs() => qc_discard_epochs();

    internal static int RetainedEpochCount(Guid session, ReplayEpochGameProof proof, out long bytes)
    {
        bytes = 0;
        if (session == Guid.Empty || !ReplayEpochContinuity.IsValidProof(proof) ||
            proof.PermissionRevision is < 0 or > uint.MaxValue) return 0;
        ulong sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 0);
        if (sessionToken == 0) sessionToken = BitConverter.ToUInt64(session.ToByteArray(), 8);
        ulong grant = ((ulong)(uint)proof.DisableRevision << 32) | (uint)proof.PermissionRevision;
        return qc_retained_epoch_count(sessionToken, grant, EpochScope(proof), out bytes);
    }

    internal static bool VideoSourceLost() => qc_video_source_lost() != 0;
    internal static bool EpochRotationPending() => qc_epoch_rotation_pending() != 0;
    internal static bool HasVideoOutput() => qc_has_video_output() != 0;
    internal static bool TestAuthorizedVideoInvalidation(bool stall) => qc_test_authorized_video_invalidation(stall ? 1 : 0) != 0;

    /// <summary>Starts screen capture again (the display changed, so the running capture may have gone stale).</summary>
    public static void RestartVideo() => qc_restart_video();

    /// <summary>Records <paramref name="window"/> (a game's) instead of the monitor at this size and bitrate; zero goes back to the monitor.</summary>
    public static void SetCaptureWindow(IntPtr window, int width, int height, int videoKbps) =>
        qc_set_capture_window((ulong)window.ToInt64(), width, height, videoKbps);

    /// <summary>Tests: makes the capture thread hang, the way a stuck capture call would.</summary>
    internal static void TestFreezeVideo(int milliseconds) => qc_test_freeze_video(milliseconds);

    /// <summary>Tests: counts keyframes smaller than this many bytes as a flat (black) picture.</summary>
    internal static void TestFlatBelow(int bytes) => qc_test_flat_below(bytes);

    /// <summary>Tests: where a process's sound was found in Desktop's capture to take it out, or null while it isn't.</summary>
    internal static long? TestSubtractOffset(uint pid) => qc_test_subtract_offset(pid) is var offset && offset != long.MinValue ? offset : null;

    /// <summary>Tests: plays quiet noise from this process for a while (returns at once).</summary>
    internal static void TestPlayNoise(int milliseconds, float amplitude) => qc_test_play_noise(milliseconds, amplitude);

    /// <summary>Which apps each track records; see <see cref="AudioRouting"/>.</summary>
    public static void SetAudioProcesses(AudioRouting.Plan plan) =>
        qc_set_audio_processes(plan.Exclude, [.. plan.Desktop], plan.Desktop.Count, [.. plan.Chat], plan.Chat.Count, [.. plan.Music], plan.Music.Count,
            [.. plan.Subtract], plan.Subtract.Count);

    public static string? Save(string path, int seconds, string title)
    {
        var error = new char[1024];
        return qc_save(path, seconds, title, error, error.Length) != 0 ? null : new string(error).TrimEnd('\0');
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplayPosition
    {
        public long Session, EventUs, FrameUs, Sequence, FirstSequence, Source;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplaySpan
    {
        public long FirstUs, LastUs, AvailableUs, DurationUs, FirstSource, LastSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplayAnchor
    {
        public long Sequence, OffsetUs;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReplayVideo
    {
        public int Width, Height, FpsNumerator, FpsDenominator;
    }

    internal static ReplayPosition? GetReplayPosition() =>
        qc_get_replay_position(out var position) != 0 ? position : null;

    internal static string? SaveReplay(string path, int seconds, string title, ReplayPosition position, bool bounded,
        ReplayAnchor[] anchors, out ReplaySpan span) =>
        SaveReplay(path, seconds, title, position, bounded, anchors, out span, out _);

    internal static string? SaveReplay(string path, int seconds, string title, ReplayPosition position, bool bounded,
        ReplayAnchor[] anchors, out ReplaySpan span, out ReplayVideo video)
    {
        var error = new char[1024];
        return qc_save_replay_with_video(path, seconds, title, position.Session, bounded ? position.EventUs : -1, out span,
            anchors, anchors.Length, out video, error, error.Length) != 0 ? null : new string(error).TrimEnd('\0');
    }

    public static Stats GetStats()
    {
        qc_get_stats(out var stats);
        return stats;
    }

    public static string VideoDiagnostics()
    {
        var text = new char[8192];
        qc_video_diagnostics(text, text.Length);
        return new string(text).TrimEnd('\0');
    }

    public static string DisplayDiagnostics()
    {
        var text = new char[8192];
        qc_display_diagnostics(text, text.Length);
        return new string(text).TrimEnd('\0');
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool SetDllDirectory(string path);
    [DllImport(Dll)] private static extern void qc_set_diagnostic_log(LogCallback? callback);
    [DllImport(Dll)] private static extern int qc_enum_outputs([Out] Output[] outputs, int max);
    [DllImport(Dll)] private static extern int qc_enum_microphones([Out] AudioDevice[] devices, int max);
    [DllImport(Dll)] private static extern void qc_enum_audio_endpoints(EndpointCallback callback);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_start(ref Config config, [Out] char[] error, int errorLength);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_start_endpoints(ref Config config, [In] EndpointConfig[] endpoints,
        int count, [Out] char[] error, int errorLength);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_start_with_display(ref Config config, [In] EndpointConfig[] endpoints,
        int count, ref VideoOptions video, string? displayIdentity, [Out] char[] error, int errorLength);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern void qc_video_diagnostics([Out] char[] text, int length);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern void qc_display_diagnostics([Out] char[] text, int length);
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    private static extern int qc_start_companion(ref Config config,
        [In] EndpointConfig[] endpoints, int count, ref VideoOptions video,
        [In] GameWindow[] windows, int windowCount, [Out] char[] error, int errorLength);
    [DllImport(Dll)]
    private static extern int qc_update_companion([In] GameWindow[] windows, int count);
    [DllImport(Dll)] private static extern void qc_stop();
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    private static extern int qc_seal_game_epoch_and_stop(ulong session, ulong epoch, ulong grant,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string scope, [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern void qc_discard_epochs();
    [DllImport(Dll)]
    private static extern int qc_retained_epoch_count(ulong session, ulong grant,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string scope, out long bytes);
    [DllImport(Dll)]
    private static extern int qc_list_game_epochs(ulong session, ulong grant,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string scope,
        [Out] EpochDescriptor[] output, int capacity, out long activeGraphStartUs);
    [DllImport(Dll, CharSet = CharSet.Unicode)]
    private static extern int qc_write_game_epoch(ulong session, ulong grant,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string scope, ulong epochId,
        string destination, [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern void qc_restart_video();
    [DllImport(Dll)] private static extern void qc_set_capture_window(ulong window, int width, int height, int videoKbps);
    [DllImport(Dll)] private static extern void qc_test_freeze_video(int milliseconds);
    [DllImport(Dll)] private static extern void qc_test_flat_below(int bytes);
    [DllImport(Dll)] private static extern void qc_set_audio_processes(uint desktopExclude, uint[] desktopInclude, int desktopCount,
        uint[] chat, int chatCount, uint[] music, int musicCount, uint[] subtract, int subtractCount);
    [DllImport(Dll)] private static extern long qc_test_subtract_offset(uint pid);
    [DllImport(Dll)] private static extern void qc_test_play_noise(int milliseconds, float amplitude);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_start_with_source(ref Config config,
        [In] EndpointConfig[] endpoints, int count, ref VideoOptions video, string? displayIdentity, in GameWindow game,
        [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern int qc_video_source_lost();
    [DllImport(Dll)] private static extern int qc_epoch_rotation_pending();
    [DllImport(Dll)] private static extern int qc_has_video_output();
    [DllImport(Dll)] private static extern int qc_test_authorized_video_invalidation(int stall);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_save(string path, int seconds, string title, [Out] char[] error, int errorLength);
    [DllImport(Dll)] private static extern int qc_get_stats(out Stats stats);
    [DllImport(Dll)] private static extern int qc_get_replay_position(out ReplayPosition position);
    [DllImport(Dll, CharSet = CharSet.Unicode)] private static extern int qc_save_replay_with_video(string path, int seconds, string title,
        long session, long requestUs, out ReplaySpan span, [In, Out] ReplayAnchor[] anchors, int count, out ReplayVideo video,
        [Out] char[] error, int errorLength);
}
