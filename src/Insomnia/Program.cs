using System.Security.Principal;
using Insomnia.App;
using Insomnia.Core;
using Insomnia.Platform;
using Insomnia.UI;

namespace Insomnia;

internal static class Program
{
    static Icon? _appIcon;
    public static Icon AppIcon => _appIcon ??= EyeArt.TrayIcon(true, false, false);

    const string Usage = """
        Insomnia for Windows — keep the PC awake for a set time, lid closed or not.

          Insomnia.exe                 run the tray app
          Insomnia.exe --start [DUR]   start a session in the running app (default preset if no DUR)
          Insomnia.exe --extend DUR    add time to the running session
          Insomnia.exe --end           end the session and undo everything
          Insomnia.exe --status        print the running app's status as JSON
          Insomnia.exe --quit          end any session and quit the app
          Insomnia.exe --restore       undo everything Insomnia journaled (used by uninstall)
          Insomnia.exe --backstop      the recovery task's check (run by Task Scheduler)
          Insomnia.exe --version

        DUR: 90 (minutes), 90m, 4h, 1d, 1d2h30m. A session runs 30 days at most.
        """;

    [STAThread]
    static int Main(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        var rest = string.Join(' ', args.Skip(1));
        switch (verb)
        {
            case "":
                return RunApp();
            case "--backstop":
                Log.Tag = "backstop";
                return Recovery.Backstop();
            case "--restore":
                Log.Tag = "restore";
                return Console_(() => Recovery.Restore(Console.Out));
            case "--start":
                return Console_(() => Send($"start {rest}".Trim()));
            case "--extend":
                return Console_(() => Send($"extend {rest}"));
            case "--end":
                return Console_(() => Send("end"));
            case "--status":
                return Console_(() => Send("status"));
            case "--quit":
                return Console_(() => Send("quit"));
            case "--show":
                return Console_(() => Send("show"));
#if LID_SIMULATION
            case "--simulate-lid":
                return Console_(() => Send($"lid {rest}"));
            case "--render-ui":
                return Console_(() => UiSnapshots.Render(rest.Length > 0 ? rest : "."));
#endif
            case "--version":
                return Console_(() =>
                {
                    Console.WriteLine($"Insomnia {typeof(Program).Assembly.GetName().Version?.ToString(3)}");
                    return 0;
                });
            default:
                return Console_(() => { Console.WriteLine(Usage); return verb is "--help" or "-h" or "/?" ? 0 : 2; });
        }
    }

    /// <summary>Insomnia is a GUI exe; borrow the parent console for CLI output.</summary>
    static int Console_(Func<int> body)
    {
        // Redirected (a pipe or file, as from PowerShell or a script): use it as is.
        // Otherwise a GUI exe has no stdout, so attach to the parent's console.
        Stream? stream = null;
        var h = Native.GetStdHandle(Native.STD_OUTPUT_HANDLE);
        if (h != IntPtr.Zero && h != new IntPtr(-1) && Native.GetFileType(h) is 1 or 3 /* disk, pipe */)
            stream = Console.OpenStandardOutput();
        else if (Native.AttachConsole(Native.ATTACH_PARENT_PROCESS))
        {
            var con = Native.CreateFile("CONOUT$", 0x40000000 /* GENERIC_WRITE */, 2 /* FILE_SHARE_WRITE */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (!con.IsInvalid) stream = new FileStream(con, FileAccess.Write);
        }
        var stdout = stream is null ? StreamWriter.Null : new StreamWriter(stream) { AutoFlush = true };
        Console.SetOut(stdout);
        try { return body(); }
        finally { stdout.Flush(); }
    }

    static int Send(string line)
    {
        var reply = CommandChannel.Send(line, TimeSpan.FromSeconds(3));
        if (reply is null)
        {
            Console.WriteLine("Insomnia is not running. Start it first (Start menu → Insomnia).");
            return 3;
        }
        Console.WriteLine(reply.TrimEnd());
        return reply.StartsWith("error") || reply.StartsWith("refused") ? 1 : 0;
    }

    static int RunApp()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var single = new Mutex(true, $@"Global\Insomnia.App.{sid}", out var first);
        if (!first)
        {
            CommandChannel.Send("show", TimeSpan.FromSeconds(2));
            return 0;
        }
        Log.Tag = "app";
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error($"unhandled: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"fatal: {e.ExceptionObject}");

        var store = new Store(Paths.Default);
        var config = store.LoadConfig(out var note);
        Log.Info($"Insomnia {typeof(Program).Assembly.GetName().Version?.ToString(3)} starting ({Environment.ProcessPath})");
        // A WinForms sync context must exist before the tray app wires timers and the pipe.
        using var bootstrap = new Control();
        _ = bootstrap.Handle;
        Application.Run(new TrayApp(store, config, note));
        Log.Info("Insomnia quit");
        return 0;
    }
}

/// <summary>Recovery outside the app: the scheduled backstop, and the forced restore uninstall runs.</summary>
internal static class Recovery
{
    /// <summary>
    /// Every minute while armed. A valid, unexpired session is left alone.
    /// Otherwise the journal is undone; once nothing is left, the task removes itself.
    /// </summary>
    public static int Backstop()
    {
        var store = new Store(Paths.Default);
        using var l = RecoveryLock.TryAcquire(store.Paths, TimeSpan.FromSeconds(2));
        if (l is null) return 0; // the app (or an earlier run) is mid-transaction; try next minute
        RuntimeState state;
        try { state = store.LoadState(); }
        catch (UnreadableJournalException e) { Log.Error(e.Message); return 1; }

        var read = store.LoadSession(out var s);
        if (read == JsonStore.ReadResult.Ok && s!.EndsAt > DateTimeOffset.Now) return 0;

        var r = new Restorer(store, state).All();
        if (r.Done.Count > 0 || r.Failures.Count > 0)
            Log.Info($"backstop restored: {string.Join(", ", r.Done.Distinct())}{(r.Failures.Count > 0 ? "; failed: " + string.Join(" ", r.Failures) : "")}");

        // The session is over either way; an unreadable file is kept aside as evidence.
        try
        {
            if (read == JsonStore.ReadResult.Unreadable && File.Exists(store.Paths.Session))
                Log.Info($"unreadable session.json treated as ended; kept at {Store.SetAside(store.Paths.Session)}");
            else store.DeleteSession();
        }
        catch (IOException e) { Log.Error($"session.json: {e.Message}"); }
        if (!state.IsDirty && !File.Exists(store.Paths.Session))
        {
            BackstopTask.Remove();
            Log.Info("nothing left to undo; recovery task removed");
        }
        if (r.Done.Count > 0) Toast("Insomnia restored your sleep settings",
            "Insomnia was not running when its session ended, so the recovery task put things back.");
        return r.Failures.Count > 0 ? 1 : 0;
    }

    /// <summary>Undo everything now, whatever the session says. Used by uninstall.</summary>
    public static int Restore(TextWriter output)
    {
        var store = new Store(Paths.Default);
        using var l = RecoveryLock.TryAcquire(store.Paths, TimeSpan.FromSeconds(30));
        if (l is null) { output.WriteLine("The recovery lock is busy (is Insomnia mid-change?). Try again."); return 1; }
        RuntimeState state;
        try { state = store.LoadState(); }
        catch (UnreadableJournalException e) { output.WriteLine(e.Message); return 1; }
        var r = new Restorer(store, state).All();
        try { store.DeleteSession(); } catch (IOException) { }
        foreach (var d in r.Done.Distinct()) output.WriteLine($"restored: {d}");
        foreach (var f in r.Failures) output.WriteLine($"FAILED: {f}");
        foreach (var w in r.Waiting) output.WriteLine($"waiting: {w}");
        if (!state.IsDirty) BackstopTask.Remove();
        output.WriteLine(state.IsDirty ? "Some entries remain in state.json." : "Nothing left to undo.");
        return r.Failures.Count > 0 ? 1 : 0;
    }

    static void Toast(string title, string body)
    {
        try
        {
            using var icon = new NotifyIcon { Icon = Program.AppIcon, Visible = true, Text = "Insomnia" };
            icon.ShowBalloonTip(8000, title, body, ToolTipIcon.Info);
            var until = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(100); }
            icon.Visible = false;
        }
        catch (Exception) { }
    }
}
