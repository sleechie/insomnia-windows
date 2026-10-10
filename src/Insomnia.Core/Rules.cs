namespace Insomnia.Core;

/// <summary>A battery reading. A desktop has no battery and no floor applies.</summary>
public readonly record struct BatteryStatus(bool Present, int? Percent, bool OnAc, int Misses)
{
    public static BatteryStatus NoBattery => new(false, null, true, 0);
    public bool Unreadable => Present && Percent is null;
}

public enum LowPowerCause { Battery, Lid }

public abstract record FloorAction
{
    public sealed record EnableLowPower(LowPowerCause Cause) : FloorAction;
    public sealed record DisableLowPower : FloorAction;
    public sealed record EndSession(EndReason Reason) : FloorAction;
}

/// <summary>
/// Battery floors plus the lid row, as a pure function. Windows exposes no
/// thermal state to ordinary processes, so the macOS thermal rows have no
/// counterpart here.
/// </summary>
public static class FloorRules
{
    public static IReadOnlyList<FloorAction> Evaluate(BatteryStatus battery, bool lidClosed, bool lowPowerSetByUs, Config config)
    {
        var onBattery = battery.Present && !battery.OnAc;
        if (battery.Percent is int p && onBattery && p < config.EndFloor)
            return [new FloorAction.EndSession(EndReason.BatteryFloor)];
        if (battery.Unreadable && onBattery && battery.Misses >= 2 && config.EndFloor > 0)
            return [new FloorAction.EndSession(EndReason.BatteryUnreadable)];
        var cause = LowPowerCauseFor(battery, lidClosed, config);
        if (cause is { } c && !lowPowerSetByUs) return [new FloorAction.EnableLowPower(c)];
        if (cause is null && lowPowerSetByUs) return [new FloorAction.DisableLowPower()];
        return [];
    }

    /// <summary>The strongest cause that wants Energy Saver now (battery, then lid), or null.</summary>
    public static LowPowerCause? LowPowerCauseFor(BatteryStatus battery, bool lidClosed, Config config)
    {
        if (battery.Percent is int p && battery.Present && !battery.OnAc && p < config.LowPowerFloor) return LowPowerCause.Battery;
        // Energy Saver only acts on battery power, so a PC without one has nothing to switch.
        if (lidClosed && config.EnergySaverOnLidClose && battery.Present) return LowPowerCause.Lid;
        return null;
    }
}

/// <summary>Which running apps a lid close suspends.</summary>
public static class FreezePlanner
{
    public sealed record Candidate(int Pid, string Name, bool HasWindow, bool SameSession);

    static HashSet<string> Set(IEnumerable<string> names) =>
        new(names.Select(Normalize), StringComparer.OrdinalIgnoreCase);

    /// <summary>"Slack" and "slack.exe" name the same app.</summary>
    public static string Normalize(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    /// <summary>
    /// The freeze list always applies (except to Insomnia itself). With
    /// <paramref name="config"/>.FreezeAllApps on, every other windowed app in
    /// the user's session is added unless it is an agent app, built-in
    /// protected, or a meeting/recording app. All processes sharing an
    /// executable name go together, since Electron and Chromium apps run as
    /// many processes under one name.
    /// </summary>
    public static IReadOnlyList<Candidate> Plan(IEnumerable<Candidate> running, Config config, int selfPid)
    {
        var list = running.Where(c => c.SameSession && c.Pid != selfPid && c.Pid > 4).ToList();
        var freeze = Set(config.FreezeList);
        freeze.Remove("Insomnia.exe");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in list)
            if (freeze.Contains(Normalize(c.Name))) names.Add(Normalize(c.Name));

        if (config.FreezeAllApps)
        {
            var exempt = Set(config.AgentList.Concat(Defaults.BuiltInProtected).Concat(Defaults.MeetingApps));
            foreach (var c in list)
            {
                var n = Normalize(c.Name);
                if (c.HasWindow && !exempt.Contains(n)) names.Add(n);
            }
        }
        return list.Where(c => names.Contains(Normalize(c.Name))).ToList();
    }
}
