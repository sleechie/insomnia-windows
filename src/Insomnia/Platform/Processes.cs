using System.Diagnostics;
using System.Runtime.InteropServices;
using Insomnia.Core;

namespace Insomnia.Platform;

/// <summary>
/// Suspends and resumes processes. Every operation goes through one open
/// handle: the creation time is read from that handle and compared with the
/// journal before anything is signalled, so a pid reused by another process
/// is never touched. NtSuspendProcess/NtResumeProcess count, so a resume only
/// undoes Insomnia's own suspend, never one a debugger or another tool made.
/// </summary>
internal static class Freezer
{
    public static IEnumerable<FreezePlanner.Candidate> Running()
    {
        var mySession = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                FreezePlanner.Candidate? c = null;
                try
                {
                    var hasWindow = false;
                    try { hasWindow = p.MainWindowHandle != IntPtr.Zero; } catch (InvalidOperationException) { }
                    c = new FreezePlanner.Candidate(p.Id, p.ProcessName + ".exe", hasWindow, p.SessionId == mySession);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                if (c is not null) yield return c;
            }
        }
    }

    /// <summary>Kernel creation time of a live pid, or null if it cannot be opened.</summary>
    public static long? CreationTime(int pid)
    {
        var h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return Alive(h) && Native.GetProcessTimes(h, out var c, out _, out _, out _) ? c : null; }
        finally { Native.CloseHandle(h); }
    }

    public enum Result { Ok, Gone, NotOurs, Failed }

    public static Result Suspend(FrozenProcess entry) => Signal(entry, suspend: true);
    public static Result Resume(FrozenProcess entry) => Signal(entry, suspend: false);

    static Result Signal(FrozenProcess entry, bool suspend)
    {
        var h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, entry.Pid);
        if (h == IntPtr.Zero)
        {
            var err = Marshal.GetLastPInvokeError();
            return err == 87 /* ERROR_INVALID_PARAMETER: no such pid */ ? Result.Gone : Result.Failed;
        }
        try
        {
            if (!Alive(h)) return Result.Gone;
            if (!Native.GetProcessTimes(h, out var created, out _, out _, out _)) return Result.Failed;
            if (created != entry.CreationFileTime) return Result.NotOurs;
            var status = suspend ? Native.NtSuspendProcess(h) : Native.NtResumeProcess(h);
            return status == 0 ? Result.Ok : Result.Failed;
        }
        finally { Native.CloseHandle(h); }
    }

    static bool Alive(IntPtr h) => Native.GetExitCodeProcess(h, out var code) && code == Native.STILL_ACTIVE;
}

/// <summary>
/// Opts agent processes out of EcoQoS (Windows power throttling) — the
/// counterpart of turning App Nap off. Not persistent: it lasts as long as
/// the process, and is handed back to Windows' own heuristics at session end.
/// </summary>
internal static class PowerThrottling
{
    public static int Apply(IEnumerable<string> exeNames, bool highQos)
    {
        var names = new HashSet<string>(exeNames.Select(FreezePlanner.Normalize), StringComparer.OrdinalIgnoreCase);
        var count = 0;
        var mySession = Process.GetCurrentProcess().SessionId;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != mySession || !names.Contains(p.ProcessName + ".exe")) continue;
                    var h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION, false, p.Id);
                    if (h == IntPtr.Zero) continue;
                    try
                    {
                        var state = new Native.PROCESS_POWER_THROTTLING_STATE
                        {
                            Version = 1,
                            ControlMask = highQos ? Native.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                            StateMask = 0,
                        };
                        if (Native.SetProcessInformation(h, Native.ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf(state))) count++;
                    }
                    finally { Native.CloseHandle(h); }
                }
                catch (InvalidOperationException) { }
            }
        }
        return count;
    }
}

/// <summary>Runs a short external command with a hard timeout, no window, arguments never through a shell.</summary>
internal static class Shell
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr, bool TimedOut)
    {
        public bool Ok => !TimedOut && ExitCode == 0;
    }

    public static Result Run(string file, IEnumerable<string> args, TimeSpan timeout, string? stdin = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new Result(-1, "", "timed out", true);
            }
            p.WaitForExit();
            return new Result(p.ExitCode, outTask.Result, errTask.Result, false);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new Result(-1, "", e.Message, false);
        }
    }
}
