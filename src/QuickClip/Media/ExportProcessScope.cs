using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace QuickClip.Media;

/// <summary>Windows closes this handle on process exit, including an unexpected QuickClip exit.</summary>
internal sealed class ExportProcessScope : IDisposable
{
    private readonly SafeFileHandle _job = CreateJobObject(IntPtr.Zero, null);

    internal ExportProcessScope()
    {
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (_job.IsInvalid || !SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            int error = Marshal.GetLastWin32Error();
            _job.Dispose();
            throw new Win32Exception(error, "Could not prepare the media encoder process boundary.");
        }
    }

    internal void Attach(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.SafeHandle))
        {
            int error = Marshal.GetLastWin32Error();
            if (!process.HasExited) throw new Win32Exception(error, "Could not attach the media encoder process.");
        }
    }

    public void Dispose() => _job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong Reads, Writes, OtherOperations, BytesRead, BytesWritten, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimits limits, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
