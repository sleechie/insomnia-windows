#if LID_SIMULATION
using Insomnia.App;
using Insomnia.Core;

namespace Insomnia.UI;

/// <summary>Test builds only: renders the flyout, settings and tray icons to PNGs, offscreen.</summary>
internal static class UiSnapshots
{
    sealed class Quiet : INotifier { public void Post(string t, string b) { } }

    public static int Render(string dir)
    {
        Directory.CreateDirectory(dir);
        Paths.Default = new Paths(Path.Combine(Path.GetTempPath(), "insomnia-ui-" + Environment.ProcessId));
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var ctl = new Control();
        _ = ctl.Handle;
        var store = new Store(Paths.Default);
        using var m = new SessionManager(store, new Config(), new Quiet(), () => IntPtr.Zero);

        using (var p = new SessionPopup(m)) Snap(p, Path.Combine(dir, "popup-idle.png"));

        m.FakeSessionForSnapshot(SessionMath.Start(DateTimeOffset.Now.AddMinutes(-17), TimeSpan.FromHours(4))!, lidClosed: false);
        using (var p = new SessionPopup(m)) Snap(p, Path.Combine(dir, "popup-active.png"));

        using (var f = new SettingsForm(m))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-4000, -4000);
            f.Show();
            var tabs = f.Controls.OfType<TabControl>().First();
            for (var i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i;
                Pump(400);
                Save(f, Path.Combine(dir, $"settings-{i}-{tabs.TabPages[i].Text.Replace(' ', '-').ToLowerInvariant()}.png"));
            }
            f.Close();
        }

        foreach (var (open, light, warn, name) in new[] { (false, false, false, "tray-idle-dark"), (true, false, false, "tray-active-dark"), (true, true, false, "tray-active-light"), (true, false, true, "tray-warn-dark") })
        {
            using var icon = EyeArt.TrayIcon(open, light, warn);
            using var big = new Bitmap(64, 64);
            using (var g = Graphics.FromImage(big))
            {
                g.Clear(light ? Color.FromArgb(0xF3, 0xF3, 0xF3) : Color.FromArgb(0x20, 0x20, 0x20));
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawIcon(icon, new Rectangle(16, 16, 32, 32));
            }
            big.Save(Path.Combine(dir, name + ".png"));
        }
        using (var eye = EyeArt.Draw(256, true, Color.White, 1f, EyeArt.Accent)) eye.Save(Path.Combine(dir, "eye-256.png"));
        Console.WriteLine($"rendered to {Path.GetFullPath(dir)}");
        return 0;
    }

    static void Snap(SessionPopup p, string file)
    {
        p.ShowNear(new Point(-3000, -3000));
        Pump(1500);
        Save(p, file);
        p.Hide();
    }

    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
    }

    static void Save(Form f, string file)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(file);
    }
}
#endif
