using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Insomnia.UI;

/// <summary>
/// The Insomnia mark, drawn in code at any size: an eye with five lashes.
/// Open while a session keeps the machine awake, shut otherwise.
/// </summary>
internal static class EyeArt
{
    public static readonly Color Accent = Color.FromArgb(0x7E, 0xB6, 0xC9);

    public static Bitmap Draw(int size, bool open, Color ink, float openness = 1f, Color? pupil = null, float zoom = 1f)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var s = size / 24f * zoom;
        var stroke = Math.Max(1.25f, 1.9f * s);
        using var pen = new Pen(ink, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

        // Centred: the mark spans from the lash tips (cy - 8.8) to the lower lid (cy + 4.7).
        float cx = size / 2f, cy = size / 2f + 2f * s, halfW = 9.5f * s;
        var lidH = 6.2f * s * (open ? Math.Clamp(openness, 0f, 1f) : 0f);

        // Lower lid: always the same curve.
        using (var lower = new GraphicsPath())
        {
            lower.AddBezier(cx - halfW, cy, cx - halfW / 2, cy + 6.2f * s, cx + halfW / 2, cy + 6.2f * s, cx + halfW, cy);
            g.DrawPath(pen, lower);
        }

        if (lidH > 0.5f)
        {
            // Upper lid lifts; the iris shows inside the almond.
            using var upper = new GraphicsPath();
            upper.AddBezier(cx - halfW, cy, cx - halfW / 2, cy - lidH, cx + halfW / 2, cy - lidH, cx + halfW, cy);
            using var almond = new GraphicsPath();
            almond.AddBezier(cx - halfW, cy, cx - halfW / 2, cy - lidH, cx + halfW / 2, cy - lidH, cx + halfW, cy);
            almond.AddBezier(cx + halfW, cy, cx + halfW / 2, cy + 6.2f * s, cx - halfW / 2, cy + 6.2f * s, cx - halfW, cy);
            var r = 3.4f * s;
            var state = g.Save();
            g.SetClip(almond);
            using (var b = new SolidBrush(pupil ?? ink)) g.FillEllipse(b, cx - r, cy + 0.6f * s - r, 2 * r, 2 * r);
            g.Restore(state);
            g.DrawPath(pen, upper);
            Lashes(g, pen, s, cx, cy, halfW, lidH, up: true);
        }
        else
        {
            // Shut: lashes hang below the closed lid.
            Lashes(g, pen, s, cx, cy, halfW, 6.2f, up: false);
        }
        return bmp;
    }

    static void Lashes(Graphics g, Pen pen, float s, float cx, float cy, float halfW, float lidH, bool up)
    {
        // Points along the lid curve at t = .15 .32 .5 .68 .85, pointing outward.
        float[] ts = [0.15f, 0.32f, 0.5f, 0.68f, 0.85f];
        foreach (var t in ts)
        {
            float x0 = cx - halfW, x3 = cx + halfW;
            float x1 = cx - halfW / 2, x2 = cx + halfW / 2;
            var dy = up ? -lidH : 6.2f * s;
            float y0 = cy, y1 = cy + dy, y2 = cy + dy, y3 = cy;
            float u = 1 - t;
            var x = u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3;
            var y = u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3;
            var dirX = (t - 0.5f) * 1.6f;
            var len = 2.6f * s;
            var dirY = up ? -1f : 1f;
            var norm = MathF.Sqrt(dirX * dirX + 1);
            g.DrawLine(pen, x, y, x + dirX / norm * len, y + dirY / norm * len);
        }
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>A tray icon at the system small-icon size. The caller owns (and must dispose) the result.</summary>
    public static Icon TrayIcon(bool open, bool lightTaskbar, bool warning)
    {
        var size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var ink = lightTaskbar ? Color.FromArgb(0x20, 0x22, 0x26) : Color.White;
        using var bmp = Draw(size, open, ink, 1f, open ? Accent : null, zoom: 1.18f);
        if (warning)
        {
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var d = size * 0.42f;
            using var b = new SolidBrush(Color.FromArgb(0xF2, 0xA1, 0x3B));
            g.FillEllipse(b, size - d, 0, d, d);
        }
        var h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }
}
