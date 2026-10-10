using System.Runtime.InteropServices;

namespace Insomnia.Platform;

/// <summary>
/// Default-output mute and volume through Core Audio (IMMDevice /
/// IAudioEndpointVolume). Each device is addressed by its endpoint id, so a
/// restore always lands on the device that was muted, never on whatever is
/// the default by then.
/// </summary>
internal static class Audio
{
    public sealed record Output(string Id, string? Name, float Volume, bool Muted, bool Active);

    static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23, DEVICE_STATE_ACTIVE = 1, STGM_READ = 0;
    static Guid _ctx = Guid.NewGuid();

    public static Output? Default()
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        try
        {
            if (en.GetDefaultAudioEndpoint(eRender, eMultimedia, out var dev) != 0 || dev is null) return null;
            return Describe(dev);
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    public static Output? Find(string id)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        try
        {
            if (en.GetDevice(id, out var dev) != 0 || dev is null) return null;
            return Describe(dev);
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Mutes or restores one device. Throws when the device is gone or refuses.</summary>
    public static void Set(string id, bool muted, float? volume = null)
    {
        var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        try
        {
            Check(en.GetDevice(id, out var dev), "GetDevice");
            try
            {
                var vol = Volume(dev);
                try
                {
                    if (volume is float v) Check(vol.SetMasterVolumeLevelScalar(Math.Clamp(v, 0f, 1f), ref _ctx), "SetMasterVolumeLevelScalar");
                    Check(vol.SetMute(muted, ref _ctx), "SetMute");
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            finally { Marshal.ReleaseComObject(dev); }
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    static Output Describe(IMMDevice dev)
    {
        try
        {
            Check(dev.GetId(out var id), "GetId");
            dev.GetState(out var state);
            var active = state == DEVICE_STATE_ACTIVE;
            float level = 0; bool muted = false;
            if (active)
            {
                var vol = Volume(dev);
                try
                {
                    Check(vol.GetMasterVolumeLevelScalar(out level), "GetMasterVolumeLevelScalar");
                    Check(vol.GetMute(out muted), "GetMute");
                }
                finally { Marshal.ReleaseComObject(vol); }
            }
            return new Output(id, FriendlyName(dev), level, muted, active);
        }
        finally { Marshal.ReleaseComObject(dev); }
    }

    static IAudioEndpointVolume Volume(IMMDevice dev)
    {
        var iid = IID_IAudioEndpointVolume;
        Check(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o), "Activate");
        return (IAudioEndpointVolume)o;
    }

    static string? FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(STGM_READ, out var store) != 0 || store is null) return null;
        try
        {
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.pointer) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    static void Check(int hr, string what)
    {
        if (hr != 0) throw new COMException($"{what} failed", hr);
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCo { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// <summary>Monitor power. Windows usually blanks the built-in panel itself on lid close; this covers external screens.</summary>
internal static class Display
{
    public static void RequestOff(IntPtr anyOwnWindow)
    {
        // DefWindowProc handles SC_MONITORPOWER for any top-level window we own; 2 = power off.
        Native.SendMessageTimeout(anyOwnWindow, Native.WM_SYSCOMMAND, Native.SC_MONITORPOWER, 2, 0x0002 /* SMTO_ABORTIFHUNG */, 2000, out _);
    }

    /// <summary>
    /// True when an active monitor is attached over an external connector
    /// (HDMI, DisplayPort, USB-C…), read from WmiMonitorConnectionParams.
    /// Internal panels report INTERNAL (0x80000000), embedded DisplayPort (11)
    /// or embedded UDI (13).
    /// </summary>
    public static bool ExternalConnected()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT Active, VideoOutputTechnology FROM WmiMonitorConnectionParams");
            foreach (System.Management.ManagementObject o in q.Get())
            {
                if (o["Active"] is not true) continue;
                var tech = Convert.ToUInt32(o["VideoOutputTechnology"]);
                if (tech is not (0x80000000 or 11 or 13)) return true;
            }
        }
        catch (Exception e) when (e is System.Management.ManagementException or COMException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>A zero-distance mouse move is the reliable way to wake monitors on Windows 10/11.</summary>
    public static void Wake()
    {
        var input = new Native.INPUT { type = 0, mi = new Native.MOUSEINPUT { dwFlags = 0x0001 /* MOUSEEVENTF_MOVE */ } };
        Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
        Native.SetThreadExecutionState(Native.ExecutionState.DisplayRequired);
    }
}
