using Insomnia.Core;
using Insomnia.Platform;

namespace Insomnia.App;

/// <summary>
/// Owns the session and every change made for it. Runs on the UI thread;
/// slow probes (docker, netsh, schtasks) run on the thread pool and come
/// back here. Every change is journaled in state.json before it is made.
/// </summary>
internal sealed class SessionManager : IDisposable
{
    readonly Store _store;
    readonly INotifier _notify;
    readonly Func<IntPtr> _windowHandle;
    readonly System.Windows.Forms.Timer _deadline = new();
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 30_000 };
    readonly System.Windows.Forms.Timer _lidDebounce = new() { Interval = 2_000 };
    readonly System.Windows.Forms.Timer _retry = new() { Interval = 10_000 };
    readonly string _exePath;

    public Config Config { get; private set; }
    public Session? Session { get; private set; }
    public RuntimeState State { get; private set; } = new();
    public bool IsActive => Session is not null;

    /// <summary>Lid state as last reported (null = unknown, e.g. a desktop).</summary>
    public bool? LidClosed { get; private set; }
    bool? _pendingLid;
    bool _lidActionsApplied;
    int _lidGeneration;

    int _batteryMisses;
    LowPowerCause? _lastCause;
    bool _reminderPosted;

    public List<string> Warnings { get; } = [];
    public string? LastGap { get; set; }
    public List<BrowserThrottle.Throttled> ThrottledBrowsers { get; private set; } = [];
    public bool JournalUnreadable { get; private set; }
    public bool SimulatedLid { get; set; }

    public event Action? Changed;

    public SessionManager(Store store, Config config, INotifier notify, Func<IntPtr> windowHandle)
    {
        _store = store;
        Config = config;
        _notify = notify;
        _windowHandle = windowHandle;
        _exePath = Environment.ProcessPath ?? "Insomnia.exe";
        _deadline.Tick += (_, _) => OnDeadlineTick();
        _poll.Tick += (_, _) => EvaluateFloors(fromEvent: true);
        _lidDebounce.Tick += (_, _) => { _lidDebounce.Stop(); ApplyLid(); };
        _retry.Tick += (_, _) => { _retry.Stop(); Reconcile(); };
    }

    void Raise() => Changed?.Invoke();

    // ------------------------------------------------------------------ locking

    T? Locked<T>(Func<T?> body, string what) where T : class
    {
        using var l = RecoveryLock.TryAcquire(_store.Paths, TimeSpan.FromSeconds(5));
        if (l is null)
        {
            Log.Error($"{what}: recovery lock busy");
            Warnings.Add($"{what} could not run: the recovery task held the lock. Try again.");
            return null;
        }
        try
        {
            State = _store.LoadState();
            JournalUnreadable = false;
            return body();
        }
        catch (UnreadableJournalException e)
        {
            JournalUnreadable = true;
            Log.Error(e.Message);
            Warnings.Add(e.Message);
            return null;
        }
    }

    // ------------------------------------------------------------------ start / extend / end

    public async Task<string?> Start(TimeSpan duration)
    {
        if (IsActive) { Extend(duration); return null; }
        if (File.Exists(_store.Paths.Session) && _store.LoadSession(out _) != JsonStore.ReadResult.Ok)
            return "An unreadable session.json is still in the Insomnia folder. Move it aside, then start again.";

        var session = SessionMath.Start(DateTimeOffset.Now, duration);
        if (session is null) return "Enter a duration above zero.";

        // The recovery task exists before anything changes, so a crash at any
        // later point is undone once the deadline passes.
        try { await Task.Run(() => BackstopTask.Arm(_exePath)); }
        catch (Exception e) { Log.Error($"start refused: {e.Message}"); return $"Not started: {e.Message}"; }

        string? error = null;
        var res = Locked<object>(() =>
        {
            _store.SaveSession(session);
            try { EnsureLidOverride(); }
            catch (Exception e) when (e is PowerPlanException or IOException)
            {
                error = $"Not started: {e.Message}";
                new Restorer(_store, State).LidActions();
                _store.DeleteSession();
                return null;
            }
            return new object();
        }, "Start");
        if (res is null && error is null)
            error = JournalUnreadable ? "Not started: state.json is unreadable." : "Not started: the recovery lock was busy.";
        if (error is not null)
        {
            Log.Error(error);
            if (!State.IsDirty) _ = Task.Run(BackstopTask.Remove);
            Raise();
            return error;
        }

        Session = session;
        Warnings.RemoveAll(w => w.StartsWith("Browser not relaunched"));
        BeginRunning();
        Log.Info($"session started: {SessionMath.Short(duration)} until {session.EndsAt:yyyy-MM-dd HH:mm}");
        _ = Task.Run(BrowserThrottle.Scan).ContinueWith(t => { ThrottledBrowsers = t.Result; Raise(); },
            TaskScheduler.FromCurrentSynchronizationContext());
        Raise();
        return null;
    }

    void BeginRunning()
    {
        IdleSleepBlock.Hold();
        _reminderPosted = false;
        _deadline.Interval = 1000;
        _deadline.Start();
        _poll.Start();
        if (Config.DisablePowerThrottlingForAgents)
            _ = Task.Run(() => PowerThrottling.Apply(Config.AgentList, true))
                .ContinueWith(t => Log.Info($"power throttling off for {t.Result} agent processes"));
        EvaluateFloors(fromEvent: false);
    }

    public void Extend(TimeSpan add)
    {
        if (Session is null) return;
        var added = SessionMath.Extend(Session, DateTimeOffset.Now, add);
        if (added == 0) { _notify.Post("Already at the limit", "A session can run for 30 days at most."); return; }
        try { _store.SaveSession(Session); }
        catch (IOException e) { Log.Error($"extend not saved: {e.Message}"); }
        _reminderPosted = false;
        Log.Info($"session extended by {added}m to {Session.EndsAt:yyyy-MM-dd HH:mm}");
        Raise();
    }

    /// <summary>Ends the session and undoes everything. Returns what could not be undone (empty when clean).</summary>
    public string End(EndReason reason, bool notify = true)
    {
        if (Session is null && !State.IsDirty) return "";
        var wasActive = Session is not null;
        Session = null;
        _deadline.Stop();
        _poll.Stop();
        _lidGeneration++;
        _lidActionsApplied = false;
        _lastCause = null;
        IdleSleepBlock.Release();
        if (Config.DisablePowerThrottlingForAgents) _ = Task.Run(() => PowerThrottling.Apply(Config.AgentList, false));

        // The session file goes first: with it gone, the launch check and the
        // recovery task both treat whatever is left in the journal as owed.
        try { _store.DeleteSession(); }
        catch (IOException e) { Log.Error($"session.json not deleted: {e.Message}"); }

        string summary = "";
        var r = Locked(() => new Restorer(_store, State).All(), "End");
        if (r is null)
        {
            summary = "Restore could not run (lock busy or journal unreadable); the recovery task will retry.";
            _retry.Start();
        }
        else
        {
            summary = r.Summary();
            Warnings.RemoveAll(w => w.StartsWith("Restore incomplete"));
            if (r.Failures.Count > 0) Warnings.Add("Restore incomplete: " + string.Join(" ", r.Failures));
            if (!State.IsDirty) _ = Task.Run(BackstopTask.Remove);
        }
        Log.Info($"session ended ({reason}){(summary.Length > 0 ? ": " + summary : "")}");
        if (notify && wasActive)
            _notify.Post(r?.Failures.Count > 0 ? "Session ended — restore incomplete" : "Session ended",
                (EndReasonText.Describe(reason) + " " + summary).Trim());
        Raise();
        return r?.Failures.Count > 0 ? summary : "";
    }

    void OnDeadlineTick()
    {
        if (Session is null) return;
        var left = Session.Remaining(DateTimeOffset.Now);
        if (left <= TimeSpan.Zero) { End(EndReason.Timer); return; }
        if (!_reminderPosted && left <= TimeSpan.FromMinutes(5) && Session.EndsAt - Session.StartedAt > TimeSpan.FromMinutes(10))
        {
            _reminderPosted = true;
            _notify.Post("Insomnia ends in 5 minutes", "Click the eye to add time.");
        }
    }

    // ------------------------------------------------------------------ power plan

    /// <summary>Sets the active plan's lid-close action to "Do nothing", journaling what it had first.</summary>
    void EnsureLidOverride()
    {
        var scheme = PowerPlan.ActiveScheme();
        if (State.LidActionOverrides.Any(o => o.Scheme == scheme))
        {
            var (ac, dc) = PowerPlan.ReadLidAction(scheme);
            if (ac != PowerPlan.LidDoNothing || dc != PowerPlan.LidDoNothing)
                PowerPlan.WriteLidAction(scheme, PowerPlan.LidDoNothing, PowerPlan.LidDoNothing);
            return;
        }
        var (pac, pdc) = PowerPlan.ReadLidAction(scheme);
        State.LidActionOverrides.Add(new LidActionOverride { Scheme = scheme, PreviousAc = pac, PreviousDc = pdc });
        _store.SaveState(State);
        PowerPlan.WriteLidAction(scheme, PowerPlan.LidDoNothing, PowerPlan.LidDoNothing);
        Log.Info($"lid-close action set to Do nothing on plan {scheme} (was AC {pac}, DC {pdc})");
    }

    /// <summary>The active plan changed (user switched plans): carry the overrides to it.</summary>
    public void OnPowerSchemeChanged()
    {
        if (!IsActive) return;
        Locked<object>(() =>
        {
            try
            {
                EnsureLidOverride();
                if (State.LowPowerSetByUs) SetEnergySaverLocked(true);
            }
            catch (Exception e) when (e is PowerPlanException or IOException)
            {
                Warnings.Add($"The new power plan could not be set to stay awake on lid close: {e.Message}");
                _notify.Post("Lid close may sleep", e.Message);
            }
            return new object();
        }, "Power plan change");
        Raise();
    }

    bool SetEnergySaver(bool on) => Locked<object>(() => SetEnergySaverLocked(on) ? new object() : null, "Energy Saver") is not null;

    bool SetEnergySaverLocked(bool on)
    {
        try
        {
            if (on)
            {
                var scheme = PowerPlan.ActiveScheme();
                if (!State.EnergySaverOverrides.Any(o => o.Scheme == scheme))
                {
                    var prev = PowerPlan.ReadEnergySaverThreshold(scheme);
                    State.EnergySaverOverrides.Add(new EnergySaverOverride { Scheme = scheme, PreviousDcThreshold = prev });
                    _store.SaveState(State);
                }
                PowerPlan.WriteEnergySaverThreshold(scheme, 100);
                return true;
            }
            var r = new Restorer(_store, State);
            r.EnergySaver();
            return r.Failures.Count == 0;
        }
        catch (Exception e) when (e is PowerPlanException or IOException)
        {
            Log.Error($"energy saver {(on ? "on" : "off")}: {e.Message}");
            Warnings.Add($"Energy Saver could not be switched {(on ? "on" : "off")}: {e.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ floors

    public BatteryStatus ReadBattery(bool countMiss)
    {
        var b = Battery.Read();
        if (countMiss)
            _batteryMisses = b.Present && b.Percent is null ? _batteryMisses + 1 : 0;
        return new BatteryStatus(b.Present, b.Percent, b.OnAc, _batteryMisses);
    }

    public void EvaluateFloors(bool fromEvent)
    {
        if (!IsActive) return;
        var battery = ReadBattery(countMiss: fromEvent);
        var lidClosed = LidClosed == true && _lidActionsApplied;
        foreach (var action in FloorRules.Evaluate(battery, lidClosed, State.LowPowerSetByUs, Config))
        {
            if (!IsActive) return;
            switch (action)
            {
                case FloorAction.EndSession e:
                    _notify.Post("Session ended", EndReasonText.Describe(e.Reason));
                    End(e.Reason, notify: false);
                    return;
                case FloorAction.EnableLowPower en:
                    if (!SetEnergySaver(true)) continue;
                    _lastCause = en.Cause;
                    if (en.Cause == LowPowerCause.Battery)
                        _notify.Post("Energy Saver on", $"Battery at {battery.Percent}%, below the {Config.LowPowerFloor}% floor.");
                    else Log.Info("energy saver on (lid closed)");
                    break;
                case FloorAction.DisableLowPower:
                    if (!SetEnergySaver(false)) continue;
                    if (_lastCause == LowPowerCause.Battery)
                        _notify.Post("Energy Saver off", battery.OnAc ? "Charger connected." : "Back above the floor.");
                    else Log.Info("energy saver off");
                    _lastCause = null;
                    break;
            }
        }
        if (State.LowPowerSetByUs && FloorRules.LowPowerCauseFor(battery, lidClosed, Config) is { } c) _lastCause = c;
        Raise();
    }

    // ------------------------------------------------------------------ lid

    /// <summary>A raw lid report. Debounced 2 s; each settled change is numbered.</summary>
    public void OnLidReport(bool closed)
    {
        if (LidClosed is null && _pendingLid is null)
        {
            LidClosed = closed; // the initial report on registration: state, not an event
            Raise();
            return;
        }
        _pendingLid = closed;
        _lidDebounce.Stop();
        _lidDebounce.Start();
    }

    /// <summary>Lid simulation: a desktop has no lid state, so the first simulated report is an event too.</summary>
    public void SimulateLid(bool closed)
    {
        SimulatedLid = true;
        LidClosed ??= !closed;
        OnLidReport(closed);
    }

    void ApplyLid()
    {
        if (_pendingLid is not bool closed) return;
        _pendingLid = null;
        if (LidClosed == closed) return;
        LidClosed = closed;
        Log.Info($"lid {(closed ? "closed" : "opened")}{(SimulatedLid ? " (simulated)" : "")}");
        var gen = ++_lidGeneration;
        if (!IsActive) { Raise(); return; }
        if (!closed) { if (_lidActionsApplied) LidOpen(); }
        else if (Config.SkipLidActionsWithExternalDisplay && Display.ExternalConnected())
            Log.Info("lid closed with an external display in use; lid-close actions skipped (the machine stays awake)");
        else _ = LidClose(gen);
        Raise();
    }

    async Task LidClose(int gen)
    {
        _lidActionsApplied = true;
        var cfg = Config;
        Locked<object>(() =>
        {
            FreezeLocked(FreezePlanner.Plan(Freezer.Running(), cfg, Environment.ProcessId), docker: false);
            if (cfg.MuteOnLidClose) MuteLocked();
            return new object();
        }, "Lid close");
        EvaluateFloors(fromEvent: false);
        if (cfg.DisplayOffOnLidClose)
        {
            await Task.Delay(1000);
            if (gen == _lidGeneration && IsActive) Display.RequestOff(_windowHandle());
        }
        if (cfg.DockerRule) await DockerOnClose(gen);
        Raise();
    }

    async Task DockerOnClose(int gen)
    {
        if (!await Task.Run(Docker.DesktopRunning)) return;
        for (var i = 0; i < 2; i++)
        {
            var probe = await Task.Run(Docker.Check);
            if (gen != _lidGeneration || !IsActive) { Log.Info($"docker probe answered {probe} after the lid opened; not used"); return; }
            if (probe != Docker.Probe.Idle) { Log.Info($"docker left running ({probe})"); return; }
        }
        var names = new HashSet<string>(Docker.ProcessNames, StringComparer.OrdinalIgnoreCase);
        var targets = Freezer.Running().Where(c => c.SameSession && names.Contains(c.Name)).ToList();
        Locked<object>(() => { FreezeLocked(targets, docker: true); return new object(); }, "Docker freeze");
    }

    void FreezeLocked(IReadOnlyList<FreezePlanner.Candidate> plan, bool docker)
    {
        var already = new HashSet<int>(State.FrozenProcesses.Select(f => f.Pid));
        var entries = new List<FrozenProcess>();
        foreach (var c in plan)
        {
            if (already.Contains(c.Pid)) continue;
            if (Freezer.CreationTime(c.Pid) is not long created) continue;
            entries.Add(new FrozenProcess { Pid = c.Pid, Name = c.Name, CreationFileTime = created, Docker = docker });
        }
        if (entries.Count == 0) return;
        State.FrozenProcesses.AddRange(entries);
        _store.SaveState(State); // journal before suspending; a failed write suspends nothing (it throws)
        var frozen = 0;
        foreach (var e in entries)
        {
            if (Freezer.Suspend(e) == Freezer.Result.Ok) { e.Confirmed = true; frozen++; }
            else State.FrozenProcesses.Remove(e);
        }
        try { _store.SaveState(State); }
        catch (IOException ex)
        {
            // The confirmation could not be written: resume at once rather than leave unconfirmed suspends behind.
            Log.Error($"freeze confirmation not saved ({ex.Message}); resuming");
            foreach (var e in entries.Where(e => e.Confirmed)) Freezer.Resume(e);
            State.FrozenProcesses.RemoveAll(entries.Contains);
            Warnings.Add("Apps were not frozen: the journal could not be written.");
            return;
        }
        Log.Info($"suspended {frozen} process(es): {string.Join(", ", entries.Where(e => e.Confirmed).Select(e => e.Name).Distinct())}");
    }

    void MuteLocked()
    {
        try
        {
            var dev = Audio.Default();
            if (dev is null || !dev.Active || State.SavedAudioOutputs.Any(a => a.DeviceId == dev.Id)) return;
            if (dev.Muted) return; // nothing to do, nothing to restore
            var entry = new SavedAudioOutput { DeviceId = dev.Id, Name = dev.Name, Volume = dev.Volume, Muted = dev.Muted };
            State.SavedAudioOutputs.Add(entry);
            _store.SaveState(State);
            try { Audio.Set(dev.Id, true); }
            catch
            {
                State.SavedAudioOutputs.Remove(entry);
                _store.SaveState(State);
                throw;
            }
            Log.Info($"muted {dev.Name}");
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            Log.Error($"mute failed: {e.Message}");
        }
    }

    void LidOpen()
    {
        _lidActionsApplied = false;
        var r = Locked(() =>
        {
            var x = new Restorer(_store, State);
            x.Frozen(null);
            x.AudioOutputs();
            return x;
        }, "Lid open");
        if (r is { Failures.Count: > 0 })
        {
            Warnings.Add("Lid-open undo incomplete: " + string.Join(" ", r.Failures));
            _notify.Post("Lid-open undo incomplete", string.Join(" ", r.Failures));
        }
        if (Config.DisplayOffOnLidClose) Display.Wake();
        EvaluateFloors(fromEvent: false);
    }

    // ------------------------------------------------------------------ launch reconcile

    /// <summary>
    /// Runs at launch. A live session is resumed; an expired, missing or
    /// unreadable one has its journal undone. Busy lock or unreadable journal:
    /// nothing changes and it retries in 10 s.
    /// </summary>
    public void Reconcile()
    {
        string? setAside = null;
        Session? live = null;
        Restorer? restored = null;
        var ok = Locked<object>(() =>
        {
            var read = _store.LoadSession(out var s);
            if (read == JsonStore.ReadResult.Ok && s!.EndsAt > DateTimeOffset.Now)
            {
                live = s;
                try { EnsureLidOverride(); }
                catch (Exception e) when (e is PowerPlanException or IOException) { Warnings.Add($"Lid-close action not re-applied: {e.Message}"); }
                return new object();
            }
            restored = new Restorer(_store, State).All();
            if (read == JsonStore.ReadResult.Unreadable && File.Exists(_store.Paths.Session))
            {
                try { setAside = Store.SetAside(_store.Paths.Session); }
                catch (IOException e) { Warnings.Add($"An unreadable session.json could not be moved aside: {e.Message}"); }
            }
            else if (read == JsonStore.ReadResult.Ok && restored.Failures.Count == 0) _store.DeleteSession();
            return new object();
        }, "Launch check");

        if (ok is null) { _retry.Start(); Raise(); return; }

        if (live is not null)
        {
            Session = live;
            Log.Info($"resumed session ending {live.EndsAt:yyyy-MM-dd HH:mm}");
            _ = Task.Run(() => { try { BackstopTask.Arm(_exePath); } catch (Exception e) { Log.Error(e.Message); } });
            BeginRunning();
            if (LidClosed != true)
            {
                // The lid is open (or unknown): whatever a previous run did on lid close comes back now.
                var r = Locked(() => { var x = new Restorer(_store, State); x.Frozen(null); x.AudioOutputs(); return x; }, "Launch lid-open undo");
                if (r is { Failures.Count: > 0 }) Warnings.Add("Restore incomplete: " + string.Join(" ", r.Failures));
            }
            _notify.Post("Session resumed", $"Insomnia picked up the session that ends {live.EndsAt:ddd h:mm tt}.");
        }
        else
        {
            if (restored is { Done.Count: > 0 } || restored is { Failures.Count: > 0 })
            {
                var body = restored.Failures.Count > 0 ? "Restore incomplete: " + restored.Summary() : "Settings Insomnia changed before it last stopped are back.";
                _notify.Post(restored.Failures.Count > 0 ? "Restore incomplete" : "Restored after an unexpected exit", body);
                if (restored.Failures.Count > 0) Warnings.Add("Restore incomplete: " + string.Join(" ", restored.Failures));
            }
            if (setAside is not null)
                _notify.Post("Unreadable session file moved", $"It was treated as ended and kept at {setAside}.");
            if (!State.IsDirty) _ = Task.Run(BackstopTask.Remove);
            CheckForeignLidSetting();
        }
        Raise();
    }

    /// <summary>"Do nothing" on lid close with nothing journaled: someone else set it. Report, never undo.</summary>
    void CheckForeignLidSetting()
    {
        try
        {
            var (ac, dc) = PowerPlan.ReadLidAction(PowerPlan.ActiveScheme());
            if ((ac == 0 || dc == 0) && Battery.Read().Present)
            {
                var msg = "Closing the lid is set to \"Do nothing\" in your power plan, and Insomnia did not set it. " +
                          "Change it under Control Panel > Power Options > Choose what closing the lid does.";
                Warnings.Add(msg);
                Log.Info("lid-close action is Do nothing outside a session (not Insomnia's)");
            }
        }
        catch (PowerPlanException) { }
    }

    public void StopWaitingForAudio(string deviceId)
    {
        Locked<object>(() =>
        {
            State.SavedAudioOutputs.RemoveAll(a => a.DeviceId == deviceId);
            _store.SaveState(State);
            return new object();
        }, "Stop waiting");
        if (!IsActive && !State.IsDirty) _ = Task.Run(BackstopTask.Remove);
        Raise();
    }

    /// <summary>An audio device came back: retry any entry waiting for it.</summary>
    public void RetryWaitingAudio()
    {
        if (State.SavedAudioOutputs.Count == 0 || (IsActive && LidClosed == true)) return;
        Locked<object>(() => { new Restorer(_store, State).AudioOutputs(); return new object(); }, "Audio retry");
        if (!IsActive && !State.IsDirty) _ = Task.Run(BackstopTask.Remove);
        Raise();
    }

#if LID_SIMULATION
    /// <summary>Test builds only: show a session without touching the machine (UI snapshots).</summary>
    public void FakeSessionForSnapshot(Session s, bool lidClosed) { Session = s; LidClosed = lidClosed; }
#endif

    public void ApplyConfig(Config c)
    {
        Config = c;
        _store.SaveConfig(c);
        EvaluateFloors(fromEvent: false);
        Raise();
    }

    public async Task<string?> RelaunchBrowser(BrowserThrottle.Throttled b)
    {
        var result = await BrowserThrottle.Relaunch(b);
        Warnings.RemoveAll(w => w.StartsWith($"Browser not relaunched: {b.Name}"));
        if (result is not null) Warnings.Add($"Browser not relaunched: {b.Name}. {result}");
        ThrottledBrowsers = await Task.Run(BrowserThrottle.Scan);
        Raise();
        return result;
    }

    public void Dispose()
    {
        _deadline.Dispose(); _poll.Dispose(); _lidDebounce.Dispose(); _retry.Dispose();
    }
}

internal interface INotifier
{
    void Post(string title, string body);
}
