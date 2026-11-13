using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuickClip.Shell;

/// <summary>Routes only the bundled short chime to an exact active render endpoint.</summary>
internal static class ClipSoundOutput
{
    internal sealed record Device(string Id, string Name);
    private const uint ActiveDevices = 1;
    private const uint AutoConvertPcm = 0x80000000;
    private const uint NoPersist = 0x00080000;
    private static readonly Lazy<byte[]> Wav = new(ClipSound.Load);
    private static readonly Guid FriendlyNameFormat = new("A45C254E-DF1C-4EFD-8020-67D146A850E0");

    internal static IReadOnlyList<Device> Devices()
    {
        var result = new List<Device>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.EnumAudioEndpoints(CoreAudio.DataFlowRender, ActiveDevices, out var devices) < 0) return result;
            try
            {
                devices.GetCount(out uint count);
                for (uint index = 0; index < count; index++)
                {
                    var device = devices.Item(index);
                    try
                    {
                        if (device.GetId(out var idPtr) < 0) continue;
                        string? id;
                        try { id = Marshal.PtrToStringUni(idPtr); }
                        finally { Marshal.FreeCoTaskMem(idPtr); }
                        if (string.IsNullOrEmpty(id)) continue;
                        result.Add(new(id, FriendlyName(device) ?? "Windows output device"));
                    }
                    catch (COMException) { /* The device may have disconnected during enumeration. */ }
                    finally { Marshal.ReleaseComObject(device); }
                }
            }
            finally { Marshal.ReleaseComObject(devices); }
        }
        finally { Marshal.ReleaseComObject(enumerator); }
        return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out IntPtr rawStore) < 0) return null;
        try
        {
            var store = (IClipSoundPropertyStore)Marshal.GetObjectForIUnknown(rawStore);
            try
            {
                var key = new PropertyKey { Format = FriendlyNameFormat, Id = 14 };
                store.GetValue(ref key, out var value);
                try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null; }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        finally { Marshal.Release(rawStore); }
    }

    internal static bool Play(string? endpointId, int volume = 100, byte[]? customWav = null)
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            try
            {
                // The MMDevice ID is opaque. Query the endpoint's actual flow before opening it.
                int result = string.IsNullOrWhiteSpace(endpointId)
                    ? enumerator.GetDefaultAudioEndpoint(CoreAudio.DataFlowRender, CoreAudio.RoleMultimedia, out var device)
                    : enumerator.GetDevice(endpointId, out device);
                if (result < 0) return false;
                try
                {
                    if (device is not IClipSoundEndpoint endpoint) return false;
                    endpoint.GetDataFlow(out int flow);
                    return flow == CoreAudio.DataFlowRender && Render(device, ScalePcm(customWav ?? Wav.Value, volume));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Write("Clip sound output unavailable: " + ex.GetType().Name);
            return false;
        }
    }

    internal static byte[] ScalePcm(byte[] wave, int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (volume == 100) return wave;
        var scaled = (byte[])wave.Clone();
        for (int offset = 44; offset + 1 < scaled.Length; offset += 2)
        {
            var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(offset, 2));
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(scaled.AsSpan(offset, 2), (short)(sample * volume / 100));
        }
        return scaled;
    }

    private static bool Render(IMMDevice device, byte[] wav)
    {
        if (wav.Length < 46 || (wav.Length - 44) % 2 != 0) return false;
        var clientId = typeof(IClipSoundAudioClient).GUID;
        device.Activate(ref clientId, CoreAudio.ClsctxAll, IntPtr.Zero, out object clientObject);
        try
        {
            var client = (IClipSoundAudioClient)clientObject;
            // The embedded asset is a canonical PCM RIFF with a 16-byte format chunk and a data chunk.
            var format = new byte[18];
            wav.AsSpan(20, 16).CopyTo(format);
            uint frames = (uint)((wav.Length - 44) / 2); // 16-bit mono
            client.Initialize(0, AutoConvertPcm | NoPersist, 6_000_000, 0, format, IntPtr.Zero);
            client.GetBufferSize(out uint capacity);
            if (capacity == 0) return false;
            var renderId = typeof(IClipSoundRenderClient).GUID;
            client.GetService(ref renderId, out object renderObject);
            try
            {
                var render = (IClipSoundRenderClient)renderObject;
                uint written = 0;
                bool started = false;
                try
                {
                    var deadline = Stopwatch.StartNew();
                    while (deadline.Elapsed < TimeSpan.FromSeconds(ClipSoundCustom.MaxSeconds + 3))
                    {
                        client.GetCurrentPadding(out uint pending);
                        if (written == frames && pending == 0)
                        {
                            client.GetStreamLatency(out long latency);
                            Thread.Sleep(TimeSpan.FromTicks(Math.Clamp(latency, 0, TimeSpan.TicksPerSecond)));
                            return true;
                        }
                        uint free = capacity > pending ? capacity - pending : 0;
                        if (written < frames && free > 0)
                        {
                            uint chunk = Math.Min(free, frames - written);
                            render.GetBuffer(chunk, out var buffer);
                            Marshal.Copy(wav, 44 + (int)(written * 2), buffer, checked((int)chunk * 2));
                            render.ReleaseBuffer(chunk, 0);
                            written += chunk;
                        }
                        if (!started)
                        {
                            client.Start();
                            started = true;
                        }
                        Thread.Sleep(10);
                    }
                    return false;
                }
                finally { if (started) client.Stop(); }
            }
            finally { Marshal.ReleaseComObject(renderObject); }
        }
        finally { Marshal.ReleaseComObject(clientObject); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid Format; public uint Id; }

    // PROPVARIANT is 24 bytes on the application's x64 target.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);

    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClipSoundEndpoint
    {
        void GetDataFlow(out int flow);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClipSoundPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClipSoundAudioClient
    {
        void Initialize(int mode, uint flags, long duration, long periodicity, [In, MarshalAs(UnmanagedType.LPArray)] byte[] format, IntPtr session);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClipSoundRenderClient
    {
        void GetBuffer(uint frames, out IntPtr buffer);
        void ReleaseBuffer(uint frames, uint flags);
    }
}
