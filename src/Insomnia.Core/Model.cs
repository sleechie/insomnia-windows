using System.Text.Json.Serialization;

namespace Insomnia.Core;

/// <summary>The only thing that keeps the machine awake. Written as <c>session.json</c>.</summary>
public sealed class Session
{
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    /// <summary>Each extension in minutes, in the order it was added.</summary>
    public List<int> ExtensionsMinutes { get; set; } = [];

    [JsonIgnore]
    public bool IsValid => EndsAt > StartedAt && StartedAt.Year >= 1970 && EndsAt.Year <= 9999;
    public TimeSpan Remaining(DateTimeOffset now) => EndsAt > now ? EndsAt - now : TimeSpan.Zero;
}

/// <summary>
/// Everything Insomnia changed and must undo. Written as <c>state.json</c>
/// before each change is made, and undone from disk — never from memory —
/// by the app, by the recovery task, and by uninstall.
/// </summary>
public sealed class RuntimeState
{
    /// <summary>Power plans whose lid-close action Insomnia set to "Do nothing", with what each had.</summary>
    public List<LidActionOverride> LidActionOverrides { get; set; } = [];
    /// <summary>Power plans whose Energy Saver threshold Insomnia raised to 100%, with what each had.</summary>
    public List<EnergySaverOverride> EnergySaverOverrides { get; set; } = [];
    /// <summary>Processes Insomnia suspended, with the identity that proves a pid is still the same process.</summary>
    public List<FrozenProcess> FrozenProcesses { get; set; } = [];
    /// <summary>Each audio output a lid close muted, with what it had.</summary>
    public List<SavedAudioOutput> SavedAudioOutputs { get; set; } = [];

    [JsonIgnore]
    public bool SleepDisabledByUs => LidActionOverrides.Count > 0;
    [JsonIgnore]
    public bool LowPowerSetByUs => EnergySaverOverrides.Count > 0;
    [JsonIgnore]
    public bool DockerFrozen => FrozenProcesses.Any(p => p.Docker);

    /// <summary>True when anything is still journaled and owed an undo.</summary>
    [JsonIgnore]
    public bool IsDirty =>
        LidActionOverrides.Count > 0 || EnergySaverOverrides.Count > 0 ||
        FrozenProcesses.Count > 0 || SavedAudioOutputs.Count > 0;
}

public sealed class LidActionOverride
{
    public Guid Scheme { get; set; }
    public uint PreviousAc { get; set; }
    public uint PreviousDc { get; set; }
}

public sealed class EnergySaverOverride
{
    public Guid Scheme { get; set; }
    public uint PreviousDcThreshold { get; set; }
}

public sealed class FrozenProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Kernel creation time (FILETIME, UTC). A pid with a different creation time is another process.</summary>
    public long CreationFileTime { get; set; }
    /// <summary>False until the suspend is confirmed; an unconfirmed entry is never resumed blindly.</summary>
    public bool Confirmed { get; set; }
    public bool Docker { get; set; }
}

public sealed class SavedAudioOutput
{
    public string DeviceId { get; set; } = "";
    public string? Name { get; set; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public Guid SaveId { get; set; } = Guid.NewGuid();
}

public enum EndReason
{
    Timer,
    User,
    Quit,
    BatteryFloor,
    BatteryUnreadable,
    Backstop,
}

public static class EndReasonText
{
    public static string Describe(EndReason r) => r switch
    {
        EndReason.Timer => "Time's up.",
        EndReason.User => "Ended early.",
        EndReason.Quit => "Insomnia quit.",
        EndReason.BatteryFloor => "Battery reached the end floor.",
        EndReason.BatteryUnreadable => "The battery could not be read twice in a row, so the floor could not be applied.",
        EndReason.Backstop => "Recovered by the background task.",
        _ => r.ToString(),
    };
}
