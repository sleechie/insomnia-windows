using Insomnia.Core;
using Insomnia.Platform;

namespace Insomnia.App;

/// <summary>
/// Undoes journaled changes. Shared by the app, the recovery task and
/// uninstall. Each entry is cleared only after its undo succeeded, and the
/// journal is written after every change, so a crash midway loses nothing.
/// The caller holds the recovery lock.
/// </summary>
internal sealed class Restorer(Store store, RuntimeState state)
{
    public RuntimeState State { get; } = state;
    public List<string> Failures { get; } = [];
    /// <summary>Things that are not failures but still wait (an audio device that is unplugged).</summary>
    public List<string> Waiting { get; } = [];
    public List<string> Done { get; } = [];

    void Save() => store.SaveState(State);

    public Restorer All()
    {
        LidActions();
        EnergySaver();
        Frozen(null);
        AudioOutputs();
        return this;
    }

    public void LidActions()
    {
        foreach (var o in State.LidActionOverrides.ToList())
        {
            try
            {
                if (!PowerPlan.SchemeExists(o.Scheme))
                {
                    Log.Info($"power plan {o.Scheme} no longer exists; dropping its lid-action entry");
                }
                else
                {
                    PowerPlan.WriteLidAction(o.Scheme, o.PreviousAc, o.PreviousDc);
                    Done.Add("lid-close action restored");
                }
                State.LidActionOverrides.Remove(o);
                Save();
            }
            catch (Exception e) when (e is PowerPlanException or IOException)
            {
                Failures.Add($"Lid-close action on plan {o.Scheme} not restored: {e.Message}");
            }
        }
    }

    public void EnergySaver()
    {
        foreach (var o in State.EnergySaverOverrides.ToList())
        {
            try
            {
                if (PowerPlan.SchemeExists(o.Scheme))
                    PowerPlan.WriteEnergySaverThreshold(o.Scheme, o.PreviousDcThreshold);
                State.EnergySaverOverrides.Remove(o);
                Save();
                Done.Add("Energy Saver restored");
            }
            catch (Exception e) when (e is PowerPlanException or IOException)
            {
                Failures.Add($"Energy Saver threshold on plan {o.Scheme} not restored: {e.Message}");
            }
        }
    }

    /// <param name="docker">null = every entry; true = Docker's only; false = everything but Docker's.</param>
    public void Frozen(bool? docker)
    {
        var resumed = 0;
        foreach (var f in State.FrozenProcesses.Where(f => docker is null || f.Docker == docker).ToList())
        {
            if (!f.Confirmed)
            {
                // Journaled, but Insomnia died before confirming the suspend.
                // Resuming would undo someone else's suspend if ours never
                // happened, so the entry is dropped and reported instead.
                Failures.Add($"{f.Name} (pid {f.Pid}) may still be suspended: Insomnia stopped before confirming the suspend. " +
                             "Check it in Resource Monitor (resmon) and choose Resume Process if it shows Suspended.");
                State.FrozenProcesses.Remove(f);
                Save();
                continue;
            }
            switch (Freezer.Resume(f))
            {
                case Freezer.Result.Ok:
                    resumed++;
                    State.FrozenProcesses.Remove(f);
                    break;
                case Freezer.Result.Gone:
                case Freezer.Result.NotOurs:
                    State.FrozenProcesses.Remove(f);
                    break;
                default:
                    Failures.Add($"{f.Name} (pid {f.Pid}) could not be resumed.");
                    continue;
            }
            Save();
        }
        if (resumed > 0) Done.Add($"resumed {resumed} process{(resumed == 1 ? "" : "es")}");
    }

    public void AudioOutputs()
    {
        foreach (var a in State.SavedAudioOutputs.ToList())
        {
            try
            {
                var now = Audio.Find(a.DeviceId);
                if (now is null || !now.Active)
                {
                    Waiting.Add($"{a.Name ?? "An audio output"} is not connected; it stays muted until it reconnects and Insomnia restores it.");
                    continue;
                }
                if (now.Muted)
                    Audio.Set(a.DeviceId, a.Muted, a.Volume);
                else
                    Log.Info($"{a.Name ?? a.DeviceId} was unmuted by hand meanwhile; left as it is");
                State.SavedAudioOutputs.Remove(a);
                Save();
                Done.Add("audio restored");
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException or IOException)
            {
                Failures.Add($"{a.Name ?? "An audio output"} could not be unmuted: {e.Message}");
            }
        }
    }

    public string Summary()
    {
        var parts = new List<string>();
        if (Failures.Count > 0) parts.Add(string.Join(" ", Failures));
        if (Waiting.Count > 0) parts.Add(string.Join(" ", Waiting));
        return string.Join(" ", parts);
    }
}
