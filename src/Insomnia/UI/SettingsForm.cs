using Insomnia.App;
using Insomnia.Core;
using Insomnia.Platform;

namespace Insomnia.UI;

/// <summary>Settings: every field of config.json, applied on Save.</summary>
internal sealed class SettingsForm : Form
{
    readonly SessionManager _m;
    readonly Config _c;

    readonly NumericUpDown _defaultPreset = Num(1, Config.MaxDurationMinutes);
    readonly TextBox _freezeList = Multi();
    readonly CheckBox _freezeAll = Check("Freeze every other windowed app while the lid is closed");
    readonly Label _wouldFreeze = Note();
    readonly CheckBox _docker = Check("Pause Docker Desktop on lid close when no container is running");
    readonly CheckBox _mute = Check("Mute audio on lid close");
    readonly CheckBox _displayOff = Check("Turn monitors off on lid close");
    readonly CheckBox _energySaver = Check("Turn Energy Saver on while the lid is closed (on battery)");
    readonly CheckBox _skipExternal = Check("Skip lid-close actions while an external monitor is in use (docked)");
    readonly TextBox _agents = Multi();
    readonly CheckBox _throttle = Check("Turn off power throttling (EcoQoS) for agent apps during a session");
    readonly NumericUpDown _lowFloor = Num(5, 100, 5);
    readonly NumericUpDown _endFloor = Num(0, 95, 5);
    readonly ComboBox _hotspot = new() { DropDownStyle = ComboBoxStyle.DropDown, Width = 260 };
    readonly NumericUpDown _nudge = Num(0, 3600, 10);
    readonly TextBox _tmux = Multi();
    readonly CheckBox _tmuxEnter = Check("Press Enter after typing \"continue\"");
    readonly CheckBox _login = Check("Launch Insomnia when I sign in");
    bool _syncingFloors;

    public SettingsForm(SessionManager m)
    {
        _m = m;
        _c = m.Config.Clone();
        Text = "Insomnia Settings";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = Palette.Ui(9.5f);
        MinimumSize = new Size(620, 560);
        Size = new Size(700, 780);

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        tabs.TabPages.Add(Page("Session", SessionPage()));
        tabs.TabPages.Add(Page("Lid close", LidPage()));
        tabs.TabPages.Add(Page("Agent apps", AgentPage()));
        tabs.TabPages.Add(Page("Battery", BatteryPage()));
        tabs.TabPages.Add(Page("Network", NetworkPage()));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        var save = new Button { Text = "Save", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        var logs = new Button { Text = "Open log folder", AutoSize = true };
        logs.Click += (_, _) => System.Diagnostics.Process.Start("explorer.exe", Paths.Default.Logs);
        save.Click += (_, _) => Save();
        buttons.Controls.AddRange([save, cancel, logs]);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(tabs);
        Controls.Add(buttons);
        Load_();
    }

    static TabPage Page(string title, Control body)
    {
        var p = new TabPage(title) { Padding = new Padding(14), AutoScroll = true };
        body.Dock = DockStyle.Fill;
        p.Controls.Add(body);
        return p;
    }

    static TableLayoutPanel Stack(params Control[] items)
    {
        var t = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoScroll = true };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var i in items)
        {
            i.Margin = new Padding(0, 4, 0, 4);
            if (i is TextBox) i.Dock = DockStyle.Fill;
            t.Controls.Add(i);
        }
        return t;
    }

    static Label Head(string text) => new() { Text = text, AutoSize = true, Font = Palette.Ui(10f, FontStyle.Bold), Margin = new Padding(0, 10, 0, 2) };
    static Label Note(string text = "") => new() { Text = text, AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText };
    static CheckBox Check(string text) => new() { Text = text, AutoSize = true };
    static TextBox Multi() => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 110, AcceptsReturn = true, WordWrap = false };
    static NumericUpDown Num(int min, int max, int step = 1) => new() { Minimum = min, Maximum = max, Increment = step, Width = 90 };

    static Control Row(string label, Control c, string? suffix = null)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        f.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) });
        f.Controls.Add(c);
        if (suffix is not null) f.Controls.Add(new Label { Text = suffix, AutoSize = true, Margin = new Padding(6, 6, 0, 0) });
        return f;
    }

    Control SessionPage() => Stack(
        Head("Default length"),
        Row("An empty Enter starts", _defaultPreset, "minutes"),
        Note("Sessions are always timed: there is no \"stay awake forever\". The longest is 30 days, extensions included."),
        Head("While a session runs"),
        Note("Closing the lid does not sleep the PC: Insomnia sets the active power plan's lid-close action to \"Do nothing\" " +
             "and puts the old value back when the session ends. Idle sleep is held off too. Use a stable, ventilated surface — never a closed bag."),
        Head("App"),
        _login);

    Control LidPage()
    {
        _freezeAll.CheckedChanged += (_, _) => UpdateWouldFreeze();
        _freezeList.TextChanged += (_, _) => UpdateWouldFreeze();
        return Stack(
            Head("Apps to freeze while the lid is closed"),
            Note("One executable per line (slack.exe, Discord.exe…). Frozen apps are suspended — not closed — and resumed when the lid opens or the session ends."),
            _freezeList,
            _freezeAll,
            Note("Never picked up automatically: agent apps, terminals, editors, browsers, AI apps, password managers, VPNs, " +
                 "local databases, the Windows shell, and meeting/recording apps (Zoom, Teams, Webex, OBS, Loom…). Listing one above still freezes it."),
            _wouldFreeze,
            _docker,
            _mute,
            Note("Each muted output is restored on its own device. One that is unplugged waits until it reconnects."),
            _displayOff,
            _energySaver,
            _skipExternal);
    }

    Control AgentPage() => Stack(
        Head("Agent apps"),
        Note("Never frozen by \"freeze every other app\". One executable per line."),
        _agents,
        _throttle,
        Note("Windows quietly slows background apps to save power (EcoQoS). This opts the processes above out for the session, " +
             "and hands them back to Windows when it ends. Nothing is written to disk."),
        Head("Browsers"),
        Note("Chromium browsers throttle hidden windows, which with the lid shut can stall browser-driving agents. " +
             "If one runs without --disable-backgrounding-occluded-windows, the tray menu offers to relaunch it unthrottled."));

    Control BatteryPage()
    {
        _lowFloor.ValueChanged += (_, _) => SyncFloors(low: true);
        _endFloor.ValueChanged += (_, _) => SyncFloors(low: false);
        var b = Battery.Read();
        return Stack(
            Head("Battery floors"),
            Row("Turn Energy Saver on below", _lowFloor, "%"),
            Row("End the session below", _endFloor, "% (0 turns this off)"),
            Note("The end floor always stays below the Energy Saver floor. A battery that cannot be read twice in a row on battery also ends the session. " +
                 "These rules need Insomnia running; the recovery task does not watch the battery."),
            Note(b.Present ? $"Now: {b.Percent?.ToString() ?? "?"}%{(b.OnAc ? ", charging" : ", on battery")}{(b.EnergySaverOn ? ", Energy Saver on" : "")}."
                           : "No battery found — floors do not apply to this PC."));
    }

    Control NetworkPage()
    {
        _ = Task.Run(NetworkPath.SavedProfiles).ContinueWith(t =>
        {
            if (IsDisposed) return;
            foreach (var p in t.Result) if (!_hotspot.Items.Contains(p)) _hotspot.Items.Add(p);
        }, TaskScheduler.FromCurrentSynchronizationContext());
        return Stack(
            Head("Hotspot failover"),
            Row("Join this Wi-Fi profile when the network drops", _hotspot),
            Note("Use a saved profile: connect to your phone's hotspot once in Windows and tick \"Connect automatically\". " +
                 "Windows keeps the password; Insomnia never sees it. Retries back off from 5 s to every 30 s until the network is back or the session ends."),
            Head("Nudge agents after a long outage"),
            Row("After an outage longer than", _nudge, "seconds"),
            Note("tmux targets inside WSL (session:window.pane), one per line. Only a pane you marked with " +
                 "`tmux set-option -p -t <target> @insomnia-nudge on` gets \"continue\" typed into it."),
            _tmux,
            _tmuxEnter);
    }

    void SyncFloors(bool low)
    {
        if (_syncingFloors) return;
        _syncingFloors = true;
        if (low) _c.SetLowPowerFloor((int)_lowFloor.Value); else _c.SetEndFloor((int)_endFloor.Value);
        _lowFloor.Value = _c.LowPowerFloor;
        _endFloor.Value = _c.EndFloor;
        _syncingFloors = false;
    }

    void Load_()
    {
        _defaultPreset.Value = Math.Clamp(_c.DefaultPresetMinutes, 1, Config.MaxDurationMinutes);
        _freezeList.Text = string.Join(Environment.NewLine, _c.FreezeList);
        _freezeAll.Checked = _c.FreezeAllApps;
        _docker.Checked = _c.DockerRule;
        _mute.Checked = _c.MuteOnLidClose;
        _displayOff.Checked = _c.DisplayOffOnLidClose;
        _energySaver.Checked = _c.EnergySaverOnLidClose;
        _skipExternal.Checked = _c.SkipLidActionsWithExternalDisplay;
        _agents.Text = string.Join(Environment.NewLine, _c.AgentList);
        _throttle.Checked = _c.DisablePowerThrottlingForAgents;
        _syncingFloors = true;
        _lowFloor.Value = Math.Clamp(_c.LowPowerFloor, 5, 100);
        _endFloor.Value = Math.Clamp(_c.EndFloor, 0, 95);
        _syncingFloors = false;
        _hotspot.Text = _c.HotspotProfile;
        _nudge.Value = Math.Clamp(_c.NudgeThresholdSeconds, 0, 3600);
        _tmux.Text = string.Join(Environment.NewLine, _c.TmuxTargets);
        _tmuxEnter.Checked = _c.TmuxNudgePressesEnter;
        _login.Checked = LoginItem.IsEnabled();
        UpdateWouldFreeze();
    }

    static List<string> Lines(TextBox t) =>
        t.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    void UpdateWouldFreeze()
    {
        var probe = _c.Clone();
        probe.FreezeList = Lines(_freezeList);
        probe.FreezeAllApps = _freezeAll.Checked;
        probe.AgentList = Lines(_agents);
        _ = Task.Run(() => FreezePlanner.Plan(Freezer.Running(), probe, Environment.ProcessId).Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList())
            .ContinueWith(t =>
            {
                if (IsDisposed) return;
                _wouldFreeze.Text = t.Result.Count == 0 ? "Would freeze now: nothing running." : "Would freeze now: " + string.Join(", ", t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Save()
    {
        _c.DefaultPresetMinutes = (int)_defaultPreset.Value;
        _c.FreezeList = Lines(_freezeList);
        _c.FreezeAllApps = _freezeAll.Checked;
        _c.DockerRule = _docker.Checked;
        _c.MuteOnLidClose = _mute.Checked;
        _c.DisplayOffOnLidClose = _displayOff.Checked;
        _c.EnergySaverOnLidClose = _energySaver.Checked;
        _c.SkipLidActionsWithExternalDisplay = _skipExternal.Checked;
        _c.AgentList = Lines(_agents);
        _c.DisablePowerThrottlingForAgents = _throttle.Checked;
        _c.LowPowerFloor = (int)_lowFloor.Value;
        _c.EndFloor = (int)_endFloor.Value;
        _c.HotspotProfile = _hotspot.Text.Trim();
        _c.NudgeThresholdSeconds = (int)_nudge.Value;
        _c.TmuxTargets = Lines(_tmux);
        _c.TmuxNudgePressesEnter = _tmuxEnter.Checked;
        _c.LaunchAtLogin = _login.Checked;
        var note = _c.NormalizeFloors();
        if (note is not null) Log.Info(note);
        try { LoginItem.Set(_c.LaunchAtLogin); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            MessageBox.Show(this, $"Launch at sign-in could not be changed: {e.Message}", "Insomnia", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _m.ApplyConfig(_c);
        Log.Info("settings saved");
    }
}
