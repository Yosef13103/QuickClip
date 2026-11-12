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
        qc_read_audio_signals(buffer, buffer.Length, AudioSignalSize);

    internal static int StartMicrophoneTest(string endpointId, out long token, int durationMs = MicrophoneTestDurationMs) =>
        qc_start_mic_test(endpointId, durationMs, out token);

    internal static NativeAudioSignal? ReadMicrophoneTest(long token) =>
        qc_read_mic_test(token, out var signal, AudioSignalSize) == 1 ? signal : null;

    internal static void StopMicrophoneTest(long token) => qc_stop_mic_test(token);

    [DllImport(Dll)] private static extern int qc_read_audio_signals([Out] NativeAudioSignal[] signals, int capacity, int size);
    [DllImport(Dll, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int qc_start_mic_test(string id, int milliseconds, out long token);
    [DllImport(Dll)] private static extern int qc_read_mic_test(long token, out NativeAudioSignal signal, int size);
    [DllImport(Dll)] private static extern void qc_stop_mic_test(long token);
}
