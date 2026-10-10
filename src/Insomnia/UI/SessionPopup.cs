using System.Runtime.InteropServices;
using Insomnia.App;
using Insomnia.Core;
using Insomnia.Platform;

namespace Insomnia.UI;

/// <summary>
/// The flyout that opens from the tray eye. Idle: Days / Hours / Minutes
/// pills (Enter starts, Esc closes, empty Enter starts the default preset).
/// Running: the countdown, pills to add time, and the hold-to-end control.
/// </summary>
internal sealed class SessionPopup : Form
{
    readonly SessionManager _m;
    readonly ToolTip _tip = new() { InitialDelay = 300 };
    readonly PictureBox _eye = new() { SizeMode = PictureBoxSizeMode.Zoom };
    readonly Label _title = new();
    readonly Label _sub = new();
    readonly Label _countdown = new();
    readonly Label _until = new();
    readonly PillBox _days, _hours, _minutes;
    readonly FlowLayoutPanel _chips = new() { WrapContents = false, AutoSize = true, BackColor = Palette.Back };
    readonly Label _hint = new();
    readonly HoldToEndButton _end = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 250 };
    readonly System.Windows.Forms.Timer _blink = new() { Interval = 15 };
    float _openness, _opennessTarget, _velocity;
    bool _busy;
    DateTime _shownAt;

    public SessionPopup(SessionManager m)
    {
        _m = m;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Palette.Back;
        ForeColor = Palette.Text;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        KeyPreview = true;
        Padding = new Padding(18);
        Text = "Insomnia";

        _days = new PillBox("Days", DurationInput.Help(DurationInput.Field.Days), _tip);
        _hours = new PillBox("Hours", "Hours (Tab to move between pills)", _tip);
        _minutes = new PillBox("Minutes", "Minutes · Enter starts · Esc closes", _tip);

        _title.Font = Palette.Ui(11f, FontStyle.Bold);
        _title.AutoSize = true;
        _sub.Font = Palette.Ui(9f);
        _sub.ForeColor = Palette.Dim;
        _sub.AutoSize = true;
        _countdown.Font = Palette.Display(30f, FontStyle.Bold);
        _countdown.AutoSize = true;
        _until.Font = Palette.Ui(9.5f);
        _until.ForeColor = Palette.Dim;
        _until.AutoSize = true;
        _hint.Font = Palette.Ui(8.5f);
        _hint.ForeColor = Palette.Dim;
        _hint.AutoSize = false;
        _end.Size = new Size(150, 34);
        _end.Completed += () => { _m.End(EndReason.User); Hide(); };

        foreach (var (label, minutes) in new[] { ("30m", 30), ("1h", 60), ("2h", 120), ("4h", 240), ("8h", 480), ("1d", 1440), ("3d", 4320) })
        {
            var chip = new Chip(label);
            chip.Size = chip.Measure();
            chip.Margin = new Padding(0, 0, 6, 0);
            chip.Click += async (_, _) => await Submit(TimeSpan.FromMinutes(minutes));
            _tip.SetToolTip(chip, $"{(minutes >= 1440 ? $"{minutes / 1440} day" : SessionMath.Short(TimeSpan.FromMinutes(minutes)))}");
            _chips.Controls.Add(chip);
        }

        Controls.AddRange([_eye, _title, _sub, _countdown, _until, _days, _hours, _minutes, _chips, _hint, _end]);
        foreach (var p in new[] { _days, _hours, _minutes }) p.Box.KeyDown += OnPillKey;

        _tick.Tick += (_, _) => Refresh_();
        _blink.Tick += (_, _) => AnimateEye();
        Deactivate += (_, _) => { if ((DateTime.UtcNow - _shownAt).TotalMilliseconds > 200) Hide(); };
        _m.Changed += () => { if (Visible) Refresh_(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW: no Alt-Tab entry
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var round = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var dark = 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    public void ShowNear(Point anchor)
    {
        _hint.ForeColor = Palette.Dim;
        _hint.Text = "";
        foreach (var p in new[] { _days, _hours, _minutes }) p.Box.Text = "";
        Layout_();
        var wa = Screen.FromPoint(anchor).WorkingArea;
        var x = Math.Clamp(anchor.X - Width / 2, wa.Left + 8, wa.Right - Width - 8);
        var y = anchor.Y > wa.Top + wa.Height / 2 ? wa.Bottom - Height - 8 : wa.Top + 8;
        Location = new Point(x, y);
        _shownAt = DateTime.UtcNow;
        _openness = _m.IsActive ? 0.0f : 0f;
        Show();
        Activate();
        BringToFront();
        _tick.Start();
        Refresh_();
        _days.Box.Focus();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) { _tick.Stop(); _blink.Stop(); }
    }

    void Layout_()
    {
        var s = DeviceDpi / 96f;
        int pad = (int)(18 * s), gap = (int)(10 * s);
        var w = (int)(380 * s);
        _eye.SetBounds(pad, pad, (int)(34 * s), (int)(34 * s));
        _title.Location = new Point(_eye.Right + gap, pad - (int)(1 * s));
        _sub.Location = new Point(_eye.Right + gap, _title.Bottom + (int)(1 * s));
        var y = _eye.Bottom + (int)(14 * s);

        var active = _m.IsActive;
        _countdown.Visible = _until.Visible = _end.Visible = active;
        if (active)
        {
            _countdown.Location = new Point(pad - (int)(4 * s), y);
            y = _countdown.Bottom - (int)(2 * s);
            _until.Location = new Point(pad, y);
            _end.SetBounds(w - pad - (int)(150 * s), _countdown.Top + (int)(10 * s), (int)(150 * s), (int)(34 * s));
            y = _until.Bottom + (int)(14 * s);
        }
        var pillW = (w - 2 * pad - 2 * gap) / 3;
        var pillH = (int)(42 * s);
        _days.SetBounds(pad, y, pillW, pillH);
        _hours.SetBounds(pad + pillW + gap, y, pillW, pillH);
        _minutes.SetBounds(pad + 2 * (pillW + gap), y, pillW, pillH);
        y += pillH + gap;
        _chips.Location = new Point(pad, y);
        y += _chips.PreferredSize.Height + gap;
        _hint.SetBounds(pad, y, w - 2 * pad, (int)(36 * s));
        y += _hint.Height;
        ClientSize = new Size(w, y + pad / 2);
    }

    void Refresh_()
    {
        var active = _m.IsActive;
        if (active != _countdown.Visible) { Layout_(); }
        _opennessTarget = active ? 1f : 0f;
        if (Math.Abs(_openness - _opennessTarget) > 0.01f || Math.Abs(_velocity) > 0.01f) _blink.Start();
        DrawEye();
        _title.Text = active ? "Keeping this PC awake" : "Insomnia";
        _sub.Text = active
            ? (_m.LidClosed == true ? "Lid closed · still awake" : "Closing the lid won't sleep it")
            : "Pick how long to stay awake";
        if (active && _m.Session is { } s)
        {
            var left = s.Remaining(DateTimeOffset.Now);
            _countdown.Text = SessionMath.Countdown(left);
            _until.Text = $"until {s.EndsAt.LocalDateTime:ddd h:mm tt} · add time below";
        }
        if (_hint.ForeColor != Palette.Danger)
        {
            var warn = _m.Warnings.LastOrDefault();
            _hint.ForeColor = warn is null ? Palette.Dim : Palette.Warn;
            _hint.Text = warn ?? (active
                ? "Type more time and press Enter, or pick a chip."
                : $"Enter starts · empty Enter = {SessionMath.Short(TimeSpan.FromMinutes(_m.Config.DefaultPresetMinutes))} · up to 30 days");
        }
    }

    /// <summary>A damped spring for the lid: slow open, no bounce on close.</summary>
    void AnimateEye()
    {
        const float stiffness = 0.018f, damping = 0.82f;
        _velocity = (_velocity + (_opennessTarget - _openness) * stiffness * 4) * damping;
        _openness += _velocity;
        if (Math.Abs(_opennessTarget - _openness) < 0.005f && Math.Abs(_velocity) < 0.005f)
        {
            _openness = _opennessTarget;
            _velocity = 0;
            _blink.Stop();
        }
        DrawEye();
    }

    void DrawEye()
    {
        var size = Math.Max(16, _eye.Width);
        var old = _eye.Image;
        _eye.Image = EyeArt.Draw(size, _openness > 0.02f, Palette.Text, _openness, Palette.Accent);
        old?.Dispose();
    }

    async void OnPillKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            var (outcome, duration) = DurationInput.Read(_days.Box.Text, _hours.Box.Text, _minutes.Box.Text);
            switch (outcome)
            {
                case DurationInput.Outcome.Empty:
                    if (_m.IsActive) { Hide(); return; }
                    await Submit(TimeSpan.FromMinutes(_m.Config.DefaultPresetMinutes));
                    break;
                case DurationInput.Outcome.Zero:
                    Error("Enter a duration above zero.");
                    break;
                case DurationInput.Outcome.TooLong:
                    Error("A session can run for 30 days at most.");
                    break;
                default:
                    await Submit(duration);
                    break;
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape) { Hide(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    async Task Submit(TimeSpan duration)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_m.IsActive)
            {
                _m.Extend(duration);
                Hide();
                return;
            }
            _hint.ForeColor = Palette.Dim;
            _hint.Text = "Starting…";
            var err = await _m.Start(duration);
            if (err is not null) { Error(err); return; }
            Layout_();
            Refresh_();
            await Task.Delay(900); // let the eye open before the flyout goes
            if (Visible && ContainsFocus) Hide();
        }
        finally { _busy = false; }
    }

    void Error(string text)
    {
        _hint.ForeColor = Palette.Danger;
        _hint.Text = text;
        System.Media.SystemSounds.Beep.Play();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tick.Dispose(); _blink.Dispose(); _tip.Dispose(); _eye.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
