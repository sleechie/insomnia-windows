using System.Runtime.InteropServices;
using System.Text.Json;
using Insomnia.Core;
using Insomnia.Platform;
using Insomnia.UI;

namespace Insomnia.App;

/// <summary>The tray icon, its menu and flyout, and the event plumbing behind them.</summary>
internal sealed class TrayApp : ApplicationContext, INotifier
{
    readonly Store _store;
    readonly NotifyIcon _tray = new();
    readonly PowerEvents _power;
    readonly SessionManager _m;
    readonly SessionPopup _popup;
    readonly NetworkFailover _net;
    readonly System.Windows.Forms.Timer _tooltip = new() { Interval = 1000 };
    readonly CancellationTokenSource _cts = new();
    SettingsForm? _settings;
    Icon? _icon;
    (bool open, bool light, bool warn)? _iconKey;
    bool _quitting;
#if LID_SIMULATION
    FileSystemWatcher? _lidSim;
#endif

    public TrayApp(Store store, Config config, string? configNote)
    {
        _store = store;
        _power = new PowerEvents();
        _m = new SessionManager(store, config, this, () => _power.Handle);
        _popup = new SessionPopup(_m);
        _net = new NetworkFailover(_m, this);

        _tray.Text = "Insomnia";
        _tray.MouseUp += OnTrayMouseUp;
        _tray.BalloonTipClicked += (_, _) => ShowPopup();
        _tray.ContextMenuStrip = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()), ShowImageMargin = false };
        _tray.ContextMenuStrip.Opening += (_, e) => { BuildMenu(); e.Cancel = false; };

        _power.Lid += closed => _m.OnLidReport(closed);
        _power.PowerStatus += () => _m.EvaluateFloors(fromEvent: true);
        _power.SchemeChanged += () => _m.OnPowerSchemeChanged();
        _power.ThemeChanged += UpdateIcon;
        _power.Resumed += () => _m.EvaluateFloors(fromEvent: false);
        _power.DevicesChanged += () => _m.RetryWaitingAudio();

        _m.Changed += UpdateIcon;
        _tooltip.Tick += (_, _) => UpdateTooltip();
        _tooltip.Start();

        UpdateIcon();
        _tray.Visible = true;
        if (configNote is not null) Log.Info(configNote);

        // Reconcile once the first messages are in, so the lid state Windows
        // reports on registration is known before anything is undone.
        var once = new System.Windows.Forms.Timer { Interval = 600 };
        once.Tick += (_, _) => { once.Stop(); once.Dispose(); _m.Reconcile(); };
        once.Start();

        var ui = SynchronizationContext.Current!;
        _ = Task.Run(() => CommandChannel.Serve(HandleCommand, ui, _cts.Token));

#if LID_SIMULATION
        StartLidSimulation();
#endif
    }

    // ------------------------------------------------------------------ INotifier

    public void Post(string title, string body)
    {
        Log.Info($"notify: {title}: {body}");
        try { _tray.ShowBalloonTip(8000, title, body.Length > 250 ? body[..247] + "…" : body, ToolTipIcon.None); }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ tray

    void OnTrayMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowPopup();
    }

    void ShowPopup()
    {
        if (_popup.Visible) { _popup.Hide(); return; }
        _popup.ShowNear(Cursor.Position);
    }

    void UpdateIcon()
    {
        var key = (_m.IsActive, Theme.TaskbarIsLight(), _m.Warnings.Count > 0 || _m.JournalUnreadable);
        if (_iconKey == key) { UpdateTooltip(); return; }
        _iconKey = key;
        var old = _icon;
        _icon = EyeArt.TrayIcon(key.Item1, key.Item2, key.Item3);
        _tray.Icon = _icon;
        old?.Dispose();
        UpdateTooltip();
    }

    void UpdateTooltip()
    {
        string text;
        if (_m.Session is { } s)
            text = $"Insomnia — {SessionMath.Countdown(s.Remaining(DateTimeOffset.Now))} left\nuntil {s.EndsAt.LocalDateTime:ddd h:mm tt}";
        else
            text = "Insomnia — not keeping awake\nClick to start a session";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    void BuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        menu.ForeColor = Palette.Text;
        menu.Font = Palette.Ui(9.5f);

        void Info(string text, Color? color = null) =>
            menu.Items.Add(new ToolStripLabel(text) { ForeColor = color ?? Palette.Dim, Margin = new Padding(6, 2, 6, 2) });

        if (_m.Session is { } s)
        {
            Info($"Awake · {SessionMath.Countdown(s.Remaining(DateTimeOffset.Now))} left · until {s.EndsAt.LocalDateTime:ddd h:mm tt}", Palette.Text);
        }
        else Info("Not keeping awake", Palette.Text);

        var b = Battery.Read();
        Info($"Lid: {(_m.LidClosed switch { true => "closed", false => "open", _ => "no lid" })} · " +
             (b.Present ? $"Battery {b.Percent?.ToString() ?? "?"}%{(b.OnAc ? " (charging)" : "")}" : "AC power") +
             (b.EnergySaverOn ? " · Energy Saver on" : ""));
        if (_m.State.FrozenProcesses.Count > 0)
            Info($"Frozen: {string.Join(", ", _m.State.FrozenProcesses.Select(f => Path.GetFileNameWithoutExtension(f.Name)).Distinct().Take(6))}");
        if (_m.State.DockerFrozen) Info("Docker Desktop paused (idle)");
        if (_m.LastGap is not null) Info($"Last network gap: {_m.LastGap}");
        if (_m.SimulatedLid) Info("Lid simulation build", Palette.Warn);

        var warnings = _m.Warnings.TakeLast(4).ToList();
        if (warnings.Count > 0 || _m.ThrottledBrowsers.Count > 0 || _m.State.SavedAudioOutputs.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var w in warnings)
            {
                var item = new ToolStripMenuItem("⚠ " + (w.Length > 90 ? w[..87] + "…" : w)) { ForeColor = Palette.Warn, ToolTipText = w };
                item.Click += (_, _) => MessageBox.Show(w, "Insomnia", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                menu.Items.Add(item);
            }
            if (warnings.Count > 0)
                menu.Items.Add("Clear warnings", null, (_, _) => { _m.Warnings.Clear(); UpdateIcon(); });
            foreach (var br in _m.ThrottledBrowsers)
            {
                var copy = br;
                menu.Items.Add(new ToolStripMenuItem($"⚠ {br.Name} throttles hidden windows — relaunch unthrottled…", null, async (_, _) => await RelaunchBrowser(copy))
                    { ForeColor = Palette.Warn });
            }
            if (!_m.IsActive || _m.LidClosed != true)
                foreach (var a in _m.State.SavedAudioOutputs)
                {
                    var id = a.DeviceId;
                    menu.Items.Add($"Stop waiting for {a.Name ?? "audio output"}", null, (_, _) => _m.StopWaitingForAudio(id));
                }
        }

        menu.Items.Add(new ToolStripSeparator());
        var presets = new ToolStripMenuItem(_m.IsActive ? "Add time" : "Keep awake for");
        foreach (var minutes in _m.Config.PresetsMinutes)
        {
            var span = TimeSpan.FromMinutes(minutes);
            presets.DropDownItems.Add(SessionMath.Short(span), null, async (_, _) =>
            {
                var err = await _m.Start(span);
                if (err is not null) Post("Not started", err);
            });
        }
        menu.Items.Add(presets);
        if (_m.IsActive)
            menu.Items.Add("End session now", null, (_, _) => _m.End(EndReason.User));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Open log folder", null, (_, _) => System.Diagnostics.Process.Start("explorer.exe", _store.Paths.Logs));
        menu.Items.Add("Quit Insomnia", null, (_, _) => Quit());
    }

    async Task RelaunchBrowser(BrowserThrottle.Throttled b)
    {
        var ok = MessageBox.Show(
            $"{b.Name} will close and reopen with throttling of hidden windows turned off, using the same profile. " +
            "Its windows come back through its own session restore.\n\nRelaunch now?",
            "Insomnia", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;
        var err = await _m.RelaunchBrowser(b);
        Post(err is null ? $"{b.Name} relaunched" : "Browser not relaunched", err ?? "Hidden windows are no longer throttled.");
    }

    void ShowSettings()
    {
        if (_settings is { IsDisposed: false }) { _settings.Activate(); return; }
        _settings = new SettingsForm(_m);
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    void Quit()
    {
        if (_quitting) return;
        if (_m.IsActive || _m.State.IsDirty)
        {
            var left = _m.End(EndReason.Quit);
            if (left.Length > 0)
            {
                var r = MessageBox.Show(
                    $"Some changes could not be undone:\n\n{left}\n\nThe recovery task keeps retrying in the background. Quit anyway?",
                    "Insomnia", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
        }
        _quitting = true;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _cts.Cancel();
        _tray.Visible = false;
        _tray.Dispose();
        _net.Dispose();
        _power.Dispose();
        _m.Dispose();
        _popup.Dispose();
        _icon?.Dispose();
        base.ExitThreadCore();
    }

    // ------------------------------------------------------------------ command channel

    async Task<string> HandleCommand(string line)
    {
        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        var arg = parts.Length > 1 ? parts[1] : "";
        switch (verb)
        {
            case "start":
            case "extend":
                var d = arg.Length == 0 ? TimeSpan.FromMinutes(_m.Config.DefaultPresetMinutes) : SessionMath.ParseDuration(arg);
                if (d is null) return "error: could not read a duration (try 90m, 4h, 1d2h)";
                if (verb == "extend" && !_m.IsActive) return "error: no session to extend";
                var err = await _m.Start(d.Value);
                return err is null ? Status() : "error: " + err;
            case "end":
                var left = _m.End(EndReason.User);
                return left.Length == 0 ? Status() : "ended with problems: " + left;
            case "status":
                return Status();
            case "show":
                ShowPopup();
                return "ok";
            case "quit":
                Quit();
                return _quitting ? "ok" : "refused";
#if LID_SIMULATION
            case "lid":
                _m.SimulateLid(arg == "closed");
                return "ok";
#endif
            default:
                return "error: unknown command (start, extend, end, status, quit, show)";
        }
    }

    string Status()
    {
        var b = Battery.Read();
        var s = _m.Session;
        return JsonSerializer.Serialize(new
        {
            active = s is not null,
            endsAt = s?.EndsAt,
            remaining = s is null ? null : SessionMath.Countdown(s.Remaining(DateTimeOffset.Now)),
            lid = _m.LidClosed switch { true => "closed", false => "open", _ => "unknown" },
            battery = b.Present ? b.Percent : null,
            onAc = b.OnAc,
            energySaver = b.EnergySaverOn,
            journal = new
            {
                lidActionOverrides = _m.State.LidActionOverrides.Count,
                energySaverOverrides = _m.State.EnergySaverOverrides.Count,
                frozen = _m.State.FrozenProcesses.Select(f => $"{f.Name}:{f.Pid}").ToArray(),
                mutedOutputs = _m.State.SavedAudioOutputs.Select(a => a.Name ?? a.DeviceId).ToArray(),
            },
            warnings = _m.Warnings,
            lastGap = _m.LastGap,
            throttledBrowsers = _m.ThrottledBrowsers.Select(t => t.Name).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true });
    }

#if LID_SIMULATION
    void StartLidSimulation()
    {
        _m.SimulatedLid = true;
        Log.Info("Lid simulation build");
        var path = _store.Paths.LidSimulation;
        _lidSim = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            SynchronizingObject = _popup,
            EnableRaisingEvents = true,
        };
        void Read()
        {
            try
            {
                var v = File.ReadAllText(path).Trim();
                if (v is "closed" or "open") _m.SimulateLid(v == "closed");
            }
            catch (IOException) { }
        }
        _lidSim.Changed += (_, _) => Read();
        _lidSim.Created += (_, _) => Read();
    }
#endif

    sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Palette.Back;
        public override Color MenuBorder => Palette.Border;
        public override Color MenuItemBorder => Palette.Border;
        public override Color MenuItemSelected => Palette.PillHover;
        public override Color MenuItemSelectedGradientBegin => Palette.PillHover;
        public override Color MenuItemSelectedGradientEnd => Palette.PillHover;
        public override Color SeparatorDark => Palette.Border;
        public override Color SeparatorLight => Palette.Back;
        public override Color ImageMarginGradientBegin => Palette.Back;
        public override Color ImageMarginGradientMiddle => Palette.Back;
        public override Color ImageMarginGradientEnd => Palette.Back;
    }
}

/// <summary>
/// A hidden window that receives power events: lid switch, charger and
/// battery changes, power-plan switches, resume, theme and device changes.
/// Windows sends the current lid state right after registration.
/// </summary>
internal sealed class PowerEvents : NativeWindow, IDisposable
{
    readonly List<IntPtr> _registrations = [];
    public event Action<bool>? Lid;
    public event Action? PowerStatus;
    public event Action? SchemeChanged;
    public event Action? ThemeChanged;
    public event Action? Resumed;
    public event Action? DevicesChanged;
    const int WM_DEVICECHANGE = 0x0219;

    public PowerEvents()
    {
        // A real top-level window (not message-only) so it also receives broadcasts.
        CreateHandle(new CreateParams { Caption = "Insomnia power events", ExStyle = 0x80 /* WS_EX_TOOLWINDOW */ });
        foreach (var g in new[] { PowerGuids.LidSwitchStateChange, PowerGuids.AcDcPowerSource, PowerGuids.BatteryPercentageRemaining, PowerGuids.PowerSchemePersonality, PowerGuids.PowerSavingStatus })
        {
            var h = Native.RegisterPowerSettingNotification(Handle, g, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
            if (h != IntPtr.Zero) _registrations.Add(h);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_POWERBROADCAST)
        {
            switch ((int)m.WParam)
            {
                case Native.PBT_POWERSETTINGCHANGE:
                    var s = Marshal.PtrToStructure<Native.POWERBROADCAST_SETTING>(m.LParam);
                    if (s.PowerSetting == PowerGuids.LidSwitchStateChange) Lid?.Invoke(s.Data == 0);
                    else if (s.PowerSetting == PowerGuids.PowerSchemePersonality) SchemeChanged?.Invoke();
                    else PowerStatus?.Invoke();
                    break;
                case Native.PBT_APMPOWERSTATUSCHANGE:
                    PowerStatus?.Invoke();
                    break;
                case Native.PBT_APMRESUMEAUTOMATIC:
                    Resumed?.Invoke();
                    break;
            }
            m.Result = 1;
            return;
        }
        if (m.Msg == Native.WM_SETTINGCHANGE) ThemeChanged?.Invoke();
        if (m.Msg == WM_DEVICECHANGE) DevicesChanged?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var h in _registrations) Native.UnregisterPowerSettingNotification(h);
        _registrations.Clear();
        DestroyHandle();
    }
}
