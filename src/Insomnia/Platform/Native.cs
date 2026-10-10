using System.Runtime.InteropServices;

namespace Insomnia.Platform;

internal static partial class Native
{
    // ---- kernel32 ----
    [Flags]
    public enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000,
    }

    [LibraryImport("kernel32.dll")]
    public static partial ExecutionState SetThreadExecutionState(ExecutionState flags);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    public const uint STILL_ACTIVE = 259;

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] buffer, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ProcessIdToSessionId(int pid, out int session);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }
    public const int ProcessPowerThrottling = 4;
    public const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, uint size);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachConsole(int pid);
    public const int ATTACH_PARENT_PROCESS = -1;

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr LocalFree(IntPtr mem);

    public const int STD_OUTPUT_HANDLE = -11;

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr GetStdHandle(int which);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetFileType(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    // ---- ntdll ----
    [LibraryImport("ntdll.dll")]
    public static partial int NtSuspendProcess(IntPtr process);

    [LibraryImport("ntdll.dll")]
    public static partial int NtResumeProcess(IntPtr process);

    // ---- powrprof ----
    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, out uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid scheme, in Guid subgroup, in Guid setting, uint value);

    // ---- user32 ----
    public const int WM_POWERBROADCAST = 0x0218;
    public const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_MONITORPOWER = 0xF170;
    public const int WM_SETTINGCHANGE = 0x001A;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data; // first DWORD of the payload
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial IntPtr RegisterPowerSettingNotification(IntPtr recipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterPowerSettingNotification(IntPtr handle);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
        // pad to the size of the INPUT union (KEYBDINPUT/HARDWAREINPUT are smaller)
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint SendInput(uint count, [In] INPUT[] inputs, int size);

    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    public static string[] SplitCommandLine(string cmd)
    {
        var p = CommandLineToArgvW(cmd, out var argc);
        if (p == IntPtr.Zero) return [];
        try
        {
            var args = new string[argc];
            for (var i = 0; i < argc; i++)
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size)) ?? "";
            return args;
        }
        finally { LocalFree(p); }
    }
}

internal static class PowerGuids
{
    public static readonly Guid SubButtons = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    public static readonly Guid LidAction = new("5ca83367-6e45-459f-a27b-476b1d01c936");
    public static readonly Guid SubEnergySaver = new("de830923-a562-41af-a086-e3a2c6bad2da");
    public static readonly Guid EnergySaverThreshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");

    public static readonly Guid LidSwitchStateChange = new("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3");
    public static readonly Guid AcDcPowerSource = new("5d3e9a59-e9d5-4b00-a6bd-ff34ff516548");
    public static readonly Guid BatteryPercentageRemaining = new("a7ad8041-b45a-4cae-87a3-eecbb468a9e1");
    public static readonly Guid PowerSchemePersonality = new("245d8541-3943-4422-b025-13a784f679b7");
    public static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
}
