using System.Runtime.InteropServices;

namespace Insomnia.Platform;

/// <summary>
/// The Windows counterpart of <c>pmset</c>: values in the active power plan,
/// read and written through powrprof. Plan settings are per-machine but a
/// standard user may change them, so none of this needs elevation.
/// </summary>
internal static class PowerPlan
{
    /// <summary>LIDACTION value: 0 = Do nothing, 1 = Sleep, 2 = Hibernate, 3 = Shut down.</summary>
    public const uint LidDoNothing = 0;

    public static Guid ActiveScheme()
    {
        var rc = Native.PowerGetActiveScheme(IntPtr.Zero, out var p);
        if (rc != 0 || p == IntPtr.Zero) throw new PowerPlanException($"PowerGetActiveScheme failed ({rc})");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Native.LocalFree(p); }
    }

    public static (uint ac, uint dc) ReadLidAction(Guid scheme) =>
        (Read(true, scheme, PowerGuids.SubButtons, PowerGuids.LidAction),
         Read(false, scheme, PowerGuids.SubButtons, PowerGuids.LidAction));

    public static void WriteLidAction(Guid scheme, uint ac, uint dc)
    {
        Write(true, scheme, PowerGuids.SubButtons, PowerGuids.LidAction, ac);
        Write(false, scheme, PowerGuids.SubButtons, PowerGuids.LidAction, dc);
        ApplyIfActive(scheme);
    }

    /// <summary>
    /// Energy Saver turns itself on below this battery percentage. 100 means
    /// "always on while on battery" — the counterpart of
    /// <c>pmset -b lowpowermode 1</c>, which is also battery-only.
    /// </summary>
    public static uint ReadEnergySaverThreshold(Guid scheme) =>
        Read(false, scheme, PowerGuids.SubEnergySaver, PowerGuids.EnergySaverThreshold);

    public static void WriteEnergySaverThreshold(Guid scheme, uint percent)
    {
        Write(false, scheme, PowerGuids.SubEnergySaver, PowerGuids.EnergySaverThreshold, percent);
        ApplyIfActive(scheme);
    }

    public static bool SchemeExists(Guid scheme)
    {
        try { ReadLidAction(scheme); return true; }
        catch (PowerPlanException) { return false; }
    }

    static uint Read(bool ac, Guid scheme, Guid sub, Guid setting)
    {
        var rc = ac
            ? Native.PowerReadACValueIndex(IntPtr.Zero, scheme, sub, setting, out var v)
            : Native.PowerReadDCValueIndex(IntPtr.Zero, scheme, sub, setting, out v);
        if (rc != 0) throw new PowerPlanException($"reading {(ac ? "AC" : "DC")} {setting} on plan {scheme} failed ({rc})");
        return v;
    }

    static void Write(bool ac, Guid scheme, Guid sub, Guid setting, uint value)
    {
        var rc = ac
            ? Native.PowerWriteACValueIndex(IntPtr.Zero, scheme, sub, setting, value)
            : Native.PowerWriteDCValueIndex(IntPtr.Zero, scheme, sub, setting, value);
        if (rc != 0)
            throw new PowerPlanException(rc == 5
                ? $"Windows refused to change the power plan (access denied). A group policy may lock plan settings."
                : $"writing {(ac ? "AC" : "DC")} {setting} on plan {scheme} failed ({rc})");
    }

    /// <summary>A written value takes effect once the plan is (re)applied.</summary>
    static void ApplyIfActive(Guid scheme)
    {
        if (ActiveScheme() != scheme) return;
        var rc = Native.PowerSetActiveScheme(IntPtr.Zero, scheme);
        if (rc != 0) throw new PowerPlanException($"PowerSetActiveScheme failed ({rc})");
    }
}

internal sealed class PowerPlanException(string message) : Exception(message);

/// <summary>Keeps the machine from idle-sleeping while the app runs. Released by the OS if the process dies.</summary>
internal static class IdleSleepBlock
{
    public static void Hold() =>
        Native.SetThreadExecutionState(Native.ExecutionState.Continuous | Native.ExecutionState.SystemRequired);

    public static void Release() => Native.SetThreadExecutionState(Native.ExecutionState.Continuous);
}

/// <summary>Battery, charger and Energy Saver state from GetSystemPowerStatus.</summary>
internal static class Battery
{
    public sealed record Reading(bool Present, int? Percent, bool OnAc, bool EnergySaverOn);

    public static Reading Read()
    {
        if (!Native.GetSystemPowerStatus(out var s))
            return new Reading(true, null, false, false);
        // BatteryFlag 128 = no system battery. 255 = unknown status.
        var present = s.BatteryFlag != 128;
        int? pct = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        if (present && s.BatteryFlag == 255 && pct is null && s.ACLineStatus == 1)
            present = false; // a desktop that reports "unknown" rather than "no battery"
        var onAc = s.ACLineStatus switch { 1 => true, 0 => false, _ => !present };
        return new Reading(present, present ? pct : null, onAc, s.SystemStatusFlag == 1);
    }
}
