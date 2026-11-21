using System; using System.Runtime.InteropServices;
namespace QuickClip.Recording.Companion;
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumerator {}

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    void GetCount(out uint count);
    IMMDevice Item(uint index);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    void Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr id);
}

[ComImport, Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager
{
    void GetAudioSessionControl(IntPtr sessionGuid, uint crossProcess, out IAudioSessionControl control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint crossProcess, out ISimpleAudioVolume volume);
}

// COM interface inheritance: the base interface's methods are repeated so the vtable lines up.
[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    void GetAudioSessionControl(IntPtr sessionGuid, uint crossProcess, out IAudioSessionControl control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint crossProcess, out ISimpleAudioVolume volume);
    IAudioSessionEnumerator GetSessionEnumerator();
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    int GetCount();
    IAudioSessionControl GetSession(int index);
}

[ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr context);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr context);
    [PreserveSig] int GetGroupingParam(out Guid param);
    [PreserveSig] int SetGroupingParam(ref Guid param, IntPtr context);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    // AUDCLNT_S_NO_SINGLE_PROCESS (a success code) for sessions shared by several processes.
    [PreserveSig] int GetProcessId(out uint pid);
}

[ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}

[ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(IntPtr name, IntPtr context);
    [PreserveSig] int OnIconPathChanged(IntPtr path, IntPtr context);
    [PreserveSig] int OnSimpleVolumeChanged(float level, int muted, IntPtr context);
    [PreserveSig] int OnChannelVolumeChanged(uint count, IntPtr volumes, uint changed, IntPtr context);
    [PreserveSig] int OnGroupingParamChanged(IntPtr param, IntPtr context);
    [PreserveSig] int OnStateChanged(int state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}
