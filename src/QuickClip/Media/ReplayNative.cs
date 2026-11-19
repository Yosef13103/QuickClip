using System.Runtime.InteropServices;
using QuickClip.Recording;

namespace QuickClip.Media;

/// <summary>Packet remux only. Call within the existing media job and its source reservations.</summary>
internal static class ReplayNative
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int Report(double progress);

    internal static void FinalizeProof(string media, string pending, string output, MediaJobContext? job, CancellationToken ct)
    {
        CaptureEngine.Initialize();
        Report report = progress =>
        {
            job?.Progress(1, progress);
            return ct.IsCancellationRequested ? 1 : 0;
        };
        var error = new char[1024];
        int result = qc_finalize_replay_proof(media, pending, output, report, error, error.Length);
        ct.ThrowIfCancellationRequested();
        if (result == 0) throw new ExportException(new string(error).TrimEnd('\0'));
    }

    internal static void Join(string[] media, string[] proofs, string output, string outputProof,
        TimeSpan limit, MediaJobContext? job, CancellationToken ct)
    {
        if (media.Length != proofs.Length) throw new ArgumentException("Each replay needs its own validated proof.");
        CaptureEngine.Initialize();
        Report report = progress =>
        {
            job?.Progress(1, progress);
            return ct.IsCancellationRequested ? 1 : 0;
        };
        var error = new char[1024];
        int result = qc_join_replay_packets(media, proofs, media.Length, output, outputProof, limit.Ticks / 10, report, error, error.Length);
        ct.ThrowIfCancellationRequested();
        if (result == 0) throw new ExportException(new string(error).TrimEnd('\0'));
    }

    [DllImport("QuickClipCapture.dll", CharSet = CharSet.Unicode)]
    private static extern int qc_finalize_replay_proof(string media, string pending, string output, Report report, [Out] char[] error, int length);

    [DllImport("QuickClipCapture.dll", CharSet = CharSet.Unicode)]
    private static extern int qc_join_replay_packets(
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] media,
        [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] proofs,
        int count, string output, string outputProof, long capUs, Report report, [Out] char[] error, int length);
}
