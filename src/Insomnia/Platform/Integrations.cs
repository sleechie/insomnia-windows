using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using Insomnia.Core;
using Microsoft.Win32;

namespace Insomnia.Platform;

/// <summary>Docker Desktop's idle check: running, and <c>docker ps -q</c> answers empty.</summary>
internal static class Docker
{
    public static readonly string[] ProcessNames = ["Docker Desktop.exe", "com.docker.backend.exe", "com.docker.build.exe"];

    public static bool DesktopRunning() => Process.GetProcessesByName("Docker Desktop").Length > 0;

    public enum Probe { Idle, Busy, Failed }

    public static Probe Check()
    {
        var r = Shell.Run("docker", ["ps", "-q"], TimeSpan.FromSeconds(10));
        if (!r.Ok) return Probe.Failed;
        return string.IsNullOrWhiteSpace(r.Stdout) ? Probe.Idle : Probe.Busy;
    }
}

/// <summary>
/// Chromium throttles windows the OS reports as occluded, which on a closed
/// laptop is every window. Browser-driving agents then stall. This finds
/// Chromium browsers running without the two flags that turn it off.
/// </summary>
internal static class BrowserThrottle
{
    public static readonly (string Exe, string Name)[] Browsers =
    [
        ("chrome.exe", "Google Chrome"), ("msedge.exe", "Microsoft Edge"), ("brave.exe", "Brave"),
        ("vivaldi.exe", "Vivaldi"), ("chromium.exe", "Chromium"), ("Arc.exe", "Arc"),
    ];

    public static readonly string[] Flags = ["--disable-backgrounding-occluded-windows", "--disable-renderer-backgrounding"];

    public sealed record Throttled(string Exe, string Name, int Pid, string ExecutablePath, string[] Args);

    public static List<Throttled> Scan()
    {
        var found = new List<Throttled>();
        foreach (var (exe, name) in Browsers)
        {
            try
            {
                using var q = new ManagementObjectSearcher($"SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name = '{exe}'");
                foreach (ManagementObject o in q.Get())
                {
                    var cmd = o["CommandLine"] as string;
                    var path = o["ExecutablePath"] as string;
                    if (cmd is null || path is null || cmd.Contains("--type=")) continue; // helpers, not the browser process
                    var args = Native.SplitCommandLine(cmd).Skip(1).ToArray();
                    if (Flags.All(f => args.Contains(f, StringComparer.OrdinalIgnoreCase))) continue;
                    found.Add(new Throttled(exe, name, Convert.ToInt32(o["ProcessId"]), path, args));
                    break;
                }
            }
            catch (ManagementException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        return found;
    }

    /// <summary>
    /// Asks the browser to close (as if its windows were closed), waits up to
    /// 10 s, then starts it again with the same profile, the two flags, and
    /// session restore. Returns null on success or the reason it stopped.
    /// Nothing is launched while any instance is still running.
    /// </summary>
    public static async Task<string?> Relaunch(Throttled b)
    {
        var keep = b.Args.Where(a =>
            a.StartsWith("--user-data-dir", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--profile-directory", StringComparison.OrdinalIgnoreCase)).ToList();
        var stem = Path.GetFileNameWithoutExtension(b.Exe);
        foreach (var p in Process.GetProcessesByName(stem))
            using (p) { try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch (InvalidOperationException) { } }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName(stem).Length > 0)
            await Task.Delay(250);
        if (Process.GetProcessesByName(stem).Length > 0)
            return $"{b.Name} was still running after 10 s (a window may be asking to confirm closing). It may still quit later; then open it by hand.";

        var psi = new ProcessStartInfo(b.ExecutablePath) { UseShellExecute = false };
        foreach (var a in keep) psi.ArgumentList.Add(a);
        foreach (var f in Flags) psi.ArgumentList.Add(f);
        psi.ArgumentList.Add("--restore-last-session");
        try { Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { return $"{b.Name} could not be started: {e.Message}"; }

        deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName(stem).Length > 0) return null;
            await Task.Delay(250);
        }
        return $"{b.Name} did not start within 5 s.";
    }
}

/// <summary>Is there a usable network path right now? (Any up, non-virtual interface with a default gateway.)</summary>
internal static class NetworkPath
{
    public static bool Satisfied()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) return false;
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            try
            {
                if (ni.GetIPProperties().GatewayAddresses.Any(g =>
                        g.Address is { } a && !a.Equals(System.Net.IPAddress.Any) && !a.Equals(System.Net.IPAddress.IPv6Any)))
                    return true;
            }
            catch (NetworkInformationException) { }
        }
        return false;
    }

    /// <summary>
    /// Joins a saved Wi-Fi profile. Windows keeps the password in the profile
    /// (connect to the hotspot once by hand and tick "Connect automatically"),
    /// so Insomnia never stores or passes one.
    /// </summary>
    public static Shell.Result Join(string profile) =>
        Shell.Run("netsh", ["wlan", "connect", $"name={profile}"], TimeSpan.FromSeconds(15));

    public static List<string> SavedProfiles()
    {
        var r = Shell.Run("netsh", ["wlan", "show", "profiles"], TimeSpan.FromSeconds(10));
        if (!r.Ok) return [];
        return r.Stdout.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Contains(':') && l.Contains("Profile", StringComparison.OrdinalIgnoreCase) && !l.EndsWith(':'))
            .Select(l => l[(l.IndexOf(':') + 1)..].Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}

/// <summary>
/// Types "continue" into tmux panes inside WSL after a long outage. Only a
/// pane the user marked with <c>tmux set-option -p -t &lt;target&gt; @insomnia-nudge on</c>
/// is nudged.
/// </summary>
internal static class TmuxNudge
{
    public static (int nudged, List<string> notes) Run(IEnumerable<string> targets, bool pressEnter)
    {
        var notes = new List<string>();
        var nudged = 0;
        foreach (var t in targets.Select(t => t.Trim()).Where(t => t.Length > 0))
        {
            var pane = Tmux(["display-message", "-p", "-t", t, "#{pane_id}"]);
            if (!pane.Ok || !pane.Stdout.Trim().StartsWith('%')) { notes.Add($"{t}: no such pane"); continue; }
            var id = pane.Stdout.Trim();
            var mark = Tmux(["show-options", "-qpv", "-t", id, "@insomnia-nudge"]);
            if (mark.Stdout.Trim() != "on") { notes.Add($"{t}: not marked @insomnia-nudge on; skipped"); continue; }
            var args = new List<string> { "send-keys", "-t", id, "continue" };
            if (pressEnter) args.Add("Enter");
            if (Tmux(args).Ok) nudged++; else notes.Add($"{t}: send-keys failed");
        }
        return (nudged, notes);
    }

    static Shell.Result Tmux(IEnumerable<string> args) =>
        Shell.Run("wsl.exe", new[] { "-e", "tmux" }.Concat(args), TimeSpan.FromSeconds(10));
}

/// <summary>
/// The recovery task: a per-user Task Scheduler entry that runs
/// <c>Insomnia.exe --backstop</c> at logon and every minute while a session
/// (or anything it journaled) exists. It is created at session start and
/// removes itself once nothing is left to undo. It runs on battery.
/// </summary>
internal static class BackstopTask
{
    public const string Name = "Insomnia Recovery";

    public static void Arm(string exePath)
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss");
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Insomnia recovery: undoes Insomnia's power-plan, audio and process changes if Insomnia exits during a session. Removes itself when there is nothing to undo.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger><Enabled>true</Enabled><UserId>{Esc(user)}</UserId></LogonTrigger>
                <TimeTrigger>
                  <Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
                  <StartBoundary>{start}</StartBoundary>
                  <Enabled>true</Enabled>
                </TimeTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author"><UserId>{Esc(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec><Command>{Esc(exePath)}</Command><Arguments>--backstop</Arguments></Exec>
              </Actions>
            </Task>
            """;
        var tmp = Path.Combine(Path.GetTempPath(), $"insomnia-task-{Environment.ProcessId}.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try
        {
            var r = Shell.Run("schtasks.exe", ["/Create", "/TN", Name, "/XML", tmp, "/F"], TimeSpan.FromSeconds(30));
            if (!r.Ok) throw new InvalidOperationException($"could not create the recovery task: {(r.Stderr + r.Stdout).Trim()}");
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    public static bool Exists() =>
        Shell.Run("schtasks.exe", ["/Query", "/TN", Name], TimeSpan.FromSeconds(30)).Ok;

    public static bool Remove()
    {
        if (!Exists()) return true;
        return Shell.Run("schtasks.exe", ["/Delete", "/TN", Name, "/F"], TimeSpan.FromSeconds(30)).Ok;
    }

    static string Esc(string s) => System.Security.SecurityElement.Escape(s) ?? s;
}

/// <summary>Launch at login: the per-user Run key.</summary>
internal static class LoginItem
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Value = "Insomnia";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Value) is string s && s.Contains(Environment.ProcessPath ?? "Insomnia.exe", StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue(Value, $"\"{Environment.ProcessPath}\"");
        else if (k.GetValue(Value) is not null) k.DeleteValue(Value);
    }
}

/// <summary>Whether the taskbar uses the light theme (so the tray glyph should be dark).</summary>
internal static class Theme
{
    public static bool TaskbarIsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    public static bool AppsAreLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
    }
}
