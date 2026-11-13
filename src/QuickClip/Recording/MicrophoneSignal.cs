namespace QuickClip.Recording;

internal static class MicrophoneSignal
{
    private const float NoiseFloor = 0.001f; // -60 dBFS

    internal static bool Matches(CaptureEngine.NativeAudioSignal signal, string id) =>
        EndpointAudio.SameDevice(signal.Flow, signal.SelectedId, EndpointFlow.Microphone, id) ||
        (id.Length > 0 && signal.ActualFlow == (int)EndpointFlow.Microphone && signal.SelectedId == "" &&
            EndpointAudio.SameDevice(signal.Flow, signal.ResolvedId, EndpointFlow.Microphone, id));

    internal static double MeterValue(float peak) =>
        float.IsFinite(peak) && peak > NoiseFloor ? Math.Clamp(1 + Math.Log10(peak) / 3, 0, 1) : 0;

    internal static string Describe(CaptureEngine.NativeAudioSignal signal)
    {
        if (signal.HResult < 0) return $"{StageName(signal.Stage)} failed (0x{unchecked((uint)signal.HResult):X8})";
        if (signal.Stage is not (EndpointStage.Streaming or EndpointStage.Complete)) return "Opening microphone…";
        if (signal.Stage == EndpointStage.Complete)
            return signal.PacketCount == 0 ? "Test finished · no audio packets" :
                signal.MaxPeak > NoiseFloor ? "Test finished · signal detected" : "Test finished · no audible signal";
        if (signal.MillisecondsSincePacket is < 0 or > 500) return "No recent audio packets";
        if (signal.RecentPeak > NoiseFloor) return "Signal detected";
        return signal.WindowsPeak > NoiseFloor ? "Windows detects sound · QuickClip PCM is quiet" : "No audible signal";
    }

    private static string StageName(EndpointStage stage) => stage switch
    {
        EndpointStage.EndpointLookup => "Device lookup",
        EndpointStage.EndpointFlow => "Microphone identity check",
        EndpointStage.Initialize => "WASAPI initialization",
        EndpointStage.Event => "WASAPI event setup",
        EndpointStage.Read => "Reading audio packets",
        _ => stage.ToString(),
    };
}
