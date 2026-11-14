using System.Runtime.InteropServices;

namespace QuickClip.Recording;

internal static partial class CaptureEngine
{
    internal const int MicrophoneTestDurationMs = 8000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeAudioSignal
    {
        public EndpointFlow Flow;
        public int ActualFlow;
        public EndpointStage Stage;
        public int HResult, TrackIndex;
        public int MixHResult, MixRate, MixChannels, MixTag, MixBits, MixValidBits, MixChannelMask, MixSubtype;
        public int RequestedRate, RequestedChannels, CaptureRate, CaptureChannels, MeterHResult;
        public long PacketCount, SilentPackets, NonzeroPackets, TimestampFallbacks, Discontinuities;
        public long MillisecondsSincePacket, NonzeroMixerBlocks;
        public float LastPeak, LastRms, RecentPeak, WindowsPeak, MaxPeak, MaxRms, MaxWindowsPeak;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string SelectedId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string ResolvedId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string DefaultCaptureId;
    }

    private static readonly int AudioSignalSize = Marshal.SizeOf<NativeAudioSignal>();

    internal static int ReadAudioSignals(NativeAudioSignal[] buffer) =>
        Volatile.Read(ref _initialized) ? qc_read_audio_signals(buffer, buffer.Length, AudioSignalSize) : 0;

    /// <summary>Returns a bounded snapshot of running lanes without exposing native device IDs or PCM.</summary>
    internal static IReadOnlyList<EndpointStatus> ReadEndpointStatus()
    {
        var native = new NativeAudioSignal[EndpointAudio.MaxTracks];
        int count = ReadAudioSignals(native);
        var result = new EndpointStatus[count];
        for (int i = 0; i < count; ++i)
            result[i] = ToEndpointStatus(native[i]);
        return result;
    }

    internal static EndpointStatus ToEndpointStatus(NativeAudioSignal lane) =>
        new(lane.TrackIndex, lane.Flow, lane.Stage, lane.HResult, lane.MixRate, lane.MixChannels,
            lane.CaptureRate, lane.PacketCount, lane.MillisecondsSincePacket >= 0 ? lane.MillisecondsSincePacket : null,
            lane.LastPeak >= 0 ? lane.LastPeak : null)
        {
            MixFormat = DescribeMixFormat(lane),
            CaptureChannels = lane.CaptureChannels,
            SilentPackets = lane.SilentPackets,
            TimestampFallbacks = lane.TimestampFallbacks,
            Discontinuities = lane.Discontinuities,
            LastPacketRms = lane.LastRms >= 0 ? lane.LastRms : null,
            MaxPacketPeak = lane.MaxPeak,
            MaxPacketRms = lane.MaxRms,
        };

    private static string DescribeMixFormat(NativeAudioSignal lane)
    {
        int subtype = lane.MixTag == 0xFFFE ? lane.MixSubtype : lane.MixTag;
        return subtype switch
        {
            1 when lane.MixValidBits > 0 && lane.MixValidBits != lane.MixBits =>
                $"PCM {lane.MixValidBits}/{lane.MixBits}-bit",
            1 => $"PCM {lane.MixBits}-bit",
            3 => $"float{lane.MixBits}",
            _ => $"format 0x{subtype:X}",
        };
    }

    internal static int StartMicrophoneTest(string endpointId, out long token, int durationMs = MicrophoneTestDurationMs)
    {
        Initialize();
        return qc_start_mic_test(endpointId, durationMs, out token);
    }

    internal static NativeAudioSignal? ReadMicrophoneTest(long token) =>
        qc_read_mic_test(token, out var signal, AudioSignalSize) == 1 ? signal : null;

    internal static void StopMicrophoneTest(long token) => qc_stop_mic_test(token);

    internal static void DrainAudioOnShutdown()
    {
        if (Volatile.Read(ref _initialized)) Stop();
    }

    [DllImport(Dll)] private static extern int qc_read_audio_signals([Out] NativeAudioSignal[] signals, int capacity, int size);
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int qc_start_mic_test(string id, int milliseconds, out long token);
    [DllImport(Dll)] private static extern int qc_read_mic_test(long token, out NativeAudioSignal signal, int size);
    [DllImport(Dll)] private static extern void qc_stop_mic_test(long token);
}
