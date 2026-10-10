using System.Drawing.Drawing2D;

namespace Insomnia.UI;

internal static class Palette
{
    public static readonly Color Back = Color.FromArgb(0x1B, 0x1D, 0x21);
    public static readonly Color Pill = Color.FromArgb(0x2A, 0x2D, 0x33);
    public static readonly Color PillHover = Color.FromArgb(0x34, 0x38, 0x3F);
    public static readonly Color Border = Color.FromArgb(0x3A, 0x3E, 0x46);
    public static readonly Color Text = Color.FromArgb(0xEE, 0xF0, 0xF3);
    public static readonly Color Dim = Color.FromArgb(0x9A, 0xA1, 0xAB);
    public static readonly Color Danger = Color.FromArgb(0xE5, 0x6B, 0x5D);
    public static readonly Color Warn = Color.FromArgb(0xF2, 0xA1, 0x3B);
    public static Color Accent => EyeArt.Accent;

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Font Ui(float pt, FontStyle style = FontStyle.Regular)
    {
        foreach (var family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                var f = new Font(family, pt, style);
                if (f.Name == family) return f;
                f.Dispose();
            }
            catch (ArgumentException) { }
        }
        return new Font(FontFamily.GenericSansSerif, pt, style);
    }

    public static Font Display(float pt, FontStyle style = FontStyle.Regular)
    {
        try
        {
            var f = new Font("Segoe UI Variable Display", pt, style);
            if (f.Name == "Segoe UI Variable Display") return f;
            f.Dispose();
        }
        catch (ArgumentException) { }
        return Ui(pt, style);
    }
}

/// <summary>A rounded numeric pill: Days, Hours or Minutes. The glow fades in on focus.</summary>
internal sealed class PillBox : Panel
{
    public TextBox Box { get; } = new();
    readonly string _help;
    string _unit;
    readonly Font _unitFont = Palette.Ui(9.5f);
    /// <summary>The unit drawn inside the pill, right of the number ("days", "+ hours").</summary>
    public string Unit { get => _unit; set { if (_unit == value) return; _unit = value; PerformLayout(); Invalidate(); } }
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    float _glow;
    bool _focused;

    public PillBox(string placeholder, string help, ToolTip tip)
    {
        _help = help;
        _unit = placeholder.ToLowerInvariant();
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Palette.Back;
        Box.BorderStyle = BorderStyle.None;
        Box.BackColor = Palette.Pill;
        Box.ForeColor = Palette.Text;
        Box.Font = Palette.Ui(13f, FontStyle.Bold);
        Box.TextAlign = HorizontalAlignment.Right;
        Box.MaxLength = 4;
        Box.ShortcutsEnabled = true;
        Box.KeyPress += (_, e) => { if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true; };
        Box.TextChanged += (_, _) =>
        {
            var clean = Insomnia.Core.DurationInput.Sanitize(Box.Text);
            if (clean != Box.Text) { Box.Text = clean; Box.SelectionStart = clean.Length; }
            Invalidate();
        };
        Box.GotFocus += (_, _) => { _focused = true; _anim.Start(); };
        Box.LostFocus += (_, _) => { _focused = false; _anim.Start(); };
        _anim.Tick += (_, _) =>
        {
            var target = _focused ? 1f : 0f;
            _glow += (target - _glow) * 0.25f;
            if (Math.Abs(target - _glow) < 0.02f) { _glow = target; _anim.Stop(); }
            Invalidate();
        };
        Controls.Add(Box);
        tip.SetToolTip(this, help);
        tip.SetToolTip(Box, help);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var h = Box.PreferredHeight;
        var unitW = TextRenderer.MeasureText(_unit, _unitFont).Width;
        Box.SetBounds(12, (Height - h) / 2, Math.Max(20, Width - 24 - unitW - 4), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(2, 2, Width - 4, Height - 4);
        if (_glow > 0.01f)
        {
            for (var i = 3; i >= 1; i--)
            {
                using var gp = Palette.Rounded(RectangleF.Inflate(r, i, i), Height / 2f + i);
                using var gb = new Pen(Color.FromArgb((int)(40 * _glow / i), Palette.Accent), 2);
                g.DrawPath(gb, gp);
            }
        }
        using var p = Palette.Rounded(r, Height / 2f);
        using var b = new SolidBrush(Palette.Pill);
        g.FillPath(b, p);
        using var pen = new Pen(Blend(Palette.Border, Palette.Accent, _glow), 1.2f);
        g.DrawPath(pen, p);
        var unitRect = new Rectangle(Box.Right + 4, 0, Width - Box.Right - 14, Height);
        TextRenderer.DrawText(g, _unit, _unitFont, unitRect, Box.Text.Length > 0 ? Palette.Text : Palette.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _anim.Dispose(); _unitFont.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>A small rounded preset chip ("30m", "4h"…). Pops slightly when pressed.</summary>
internal sealed class Chip : Control
{
    bool _hover, _down;
    public Chip(string text)
    {
        Text = text;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint, true);
        BackColor = Palette.Back;
        ForeColor = Palette.Text;
        Font = Palette.Ui(9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public Size Measure()
    {
        var sz = TextRenderer.MeasureText(Text, Font);
        return new Size(sz.Width + 18, sz.Height + 10);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = _down ? 2f : 1f;
        var r = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
        using var p = Palette.Rounded(r, r.Height / 2);
        using var b = new SolidBrush(_hover ? Palette.PillHover : Palette.Pill);
        g.FillPath(b, p);
        using var pen = new Pen(Palette.Border);
        g.DrawPath(pen, p);
        TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// End control: press and hold for 1.2 s. A ring fills while held; letting go
/// early springs it back and nothing happens.
/// </summary>
internal sealed class HoldToEndButton : Control
{
    readonly System.Windows.Forms.Timer _t = new() { Interval = 15 };
    DateTime _pressedAt;
    bool _holding;
    float _progress;
    public static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(1200);
    public event Action? Completed;

    public HoldToEndButton()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint, true);
        BackColor = Palette.Back;
        Cursor = Cursors.Hand;
        Font = Palette.Ui(9f, FontStyle.Bold);
        _t.Tick += (_, _) => Step();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _holding = true;
        _pressedAt = DateTime.UtcNow;
        _t.Start();
        Capture = true;
    }

    protected override void OnMouseUp(MouseEventArgs e) { _holding = false; Capture = false; }
    protected override void OnMouseCaptureChanged(EventArgs e) { _holding = false; base.OnMouseCaptureChanged(e); }

    void Step()
    {
        if (_holding)
        {
            _progress = (float)Math.Min(1, (DateTime.UtcNow - _pressedAt) / Hold);
            if (_progress >= 1)
            {
                _holding = false; _t.Stop(); _progress = 0; Capture = false;
                Invalidate();
                Completed?.Invoke();
                return;
            }
        }
        else
        {
            _progress *= 0.75f;
            if (_progress < 0.01f) { _progress = 0; _t.Stop(); }
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(1.5f, 1.5f, Width - 3, Height - 3);
        using (var p = Palette.Rounded(r, r.Height / 2))
        {
            using var b = new SolidBrush(Palette.Pill);
            g.FillPath(b, p);
            if (_progress > 0)
            {
                var state = g.Save();
                g.SetClip(p);
                using var fill = new SolidBrush(Color.FromArgb(160, Palette.Danger));
                g.FillRectangle(fill, r.X, r.Y, r.Width * _progress, r.Height);
                g.Restore(state);
            }
            using var pen = new Pen(_progress > 0 ? Palette.Danger : Palette.Border, 1.2f);
            g.DrawPath(pen, p);
        }
        // A small square "stop" glyph, then the label.
        var gs = Height * 0.26f;
        using (var gb = new SolidBrush(Palette.Danger))
            g.FillRectangle(gb, 14, (Height - gs) / 2, gs, gs);
        TextRenderer.DrawText(g, _progress > 0 ? "Keep holding…" : "Hold to end", Font,
            new Rectangle((int)(14 + gs + 6), 0, Width - (int)(14 + gs + 10), Height), Palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _t.Dispose();
        base.Dispose(disposing);
    }
}
