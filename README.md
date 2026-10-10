<p align="center">
  <img src="docs/eye.png" alt="Insomnia: an open eye with five lashes" width="96">
</p>

<h1 align="center">Insomnia for Windows</h1>

<p align="center">
  <strong>Keep your PC awake. Give the session an end time.</strong><br>
  A Windows port of <a href="https://github.com/krishhgg/Insomnia">krishhgg/Insomnia</a>, the macOS menu bar app:
  timed awake sessions for long-running agent work, with the lid closed or not.
</p>

> **Use a stable, well-ventilated surface — never a closed bag.** This is
> experimental software. A running timer is not a safety guarantee.

## What it does

Click the eye in the system tray, type Days / Hours / Minutes, press Enter.
Until the timer runs out, the PC will not sleep — not from idle, and not when
you close the lid. When the session ends (timer, "Hold to end", Quit, or the
battery floor) everything Insomnia changed is put back.

While a session runs and the lid is closed, Insomnia (by default):

- **suspends the apps on the freeze list** (Slack, WhatsApp, Discord) so they
  stop burning battery, and resumes them when the lid opens;
- **mutes the speakers**, restoring each output on its own device when the lid opens;
- **turns Energy Saver on** (battery only), and off again when the lid opens;
- **asks monitors to power off** (Windows blanks the built-in panel itself; this covers external screens).

Optional, off by default: freeze *every* other windowed app except agent
apps, terminals, editors, browsers, AI apps, password managers, meeting apps
and the Windows shell; pause Docker Desktop when `docker ps` shows nothing
running; opt agent processes out of Windows power throttling (EcoQoS); join
a phone hotspot when the network drops, and type `continue` into marked tmux
panes (in WSL) after a long outage.

Closing the lid with an **external monitor in use** counts as docked desk use:
the session still keeps the PC awake, but nothing is frozen, muted or blanked.

## Install

Requires Windows 10 1809+ or Windows 11, x64. **No administrator rights are needed** —
everything Insomnia touches is per-user or a power-plan value a standard user may change.

1. Download `Insomnia-win-x64.zip` from the newest release (or build it, below) and unzip it.
2. In that folder, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -LaunchAtLogin
   ```

   That copies `Insomnia.exe` to `%LOCALAPPDATA%\Programs\Insomnia`, adds a
   Start menu shortcut, registers launch at sign-in (drop `-LaunchAtLogin` if
   you don't want that), and starts it. The exe is unsigned, so SmartScreen
   may ask once.
3. Find the eye in the tray (it may be in the `^` overflow — drag it onto the
   taskbar). Right-click → **Settings…** and review the lid-close actions
   before your first session. Start with a short, supervised session.

Uninstall: `powershell -ExecutionPolicy Bypass -File .\uninstall.ps1` (add `-Purge` to
also delete settings and logs). It quits the app, undoes anything still journaled,
and only then removes files.

### Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Builds on
Windows, Linux or macOS (cross-compiled):

```powershell
.\scripts\build.ps1          # -> dist\release\Insomnia.exe (self-contained, no .NET install needed to run)
.\scripts\install.ps1 -LaunchAtLogin
```

## Using it

| | |
| --- | --- |
| **Start** | Click the eye, type Days / Hours / Minutes, press Enter. Empty + Enter starts the default (4 h). Or click a chip (30m … 3d). |
| **Extend** | Click the eye during a session, type more time, Enter. |
| **End early** | Press and hold **Hold to end** for about a second. |
| **Status, presets, Settings, Quit** | Right-click the eye. Quitting ends the session. |

From a terminal — or from a coding agent that should keep your machine awake
while it works:

```powershell
Insomnia.exe --start 4h      # 90, 90m, 4h, 1d2h30m
Insomnia.exe --extend 30m
Insomnia.exe --status        # JSON
Insomnia.exe --end
```

The app must be running; the command line talks to it over a named pipe only
your account can open.

## How it works (and what it changes)

| | During a session | Put back |
| --- | --- | --- |
| Idle sleep | blocked with `SetThreadExecutionState` | released automatically when the app exits or the session ends |
| Lid close | the **active power plan's** "When I close the lid" set to *Do nothing* (AC and battery). If you switch plans mid-session, the new plan gets it too | each plan gets back exactly what it had |
| Energy Saver | the plan's Energy Saver threshold raised to 100% (= always on while on battery) | the previous threshold |
| Frozen apps | `NtSuspendProcess`, by executable name | `NtResumeProcess` |
| Audio | default output muted | that device's previous volume and mute; an output you unmuted by hand meanwhile is left alone |
| Agent apps (opt-in) | EcoQoS power throttling turned off per process | handed back to Windows |

**Every change is written to a journal (`%LOCALAPPDATA%\Insomnia\state.json`)
before it is made**, and undone from that journal — never from memory. A
process is identified by pid *and* kernel creation time, read through the same
handle that resumes it, so a pid reused by another program is never touched.
Windows suspend counts are per caller, so Insomnia's resume only undoes its own suspend.

**Recovery when Insomnia isn't running.** A session start first creates a
per-user scheduled task, *Insomnia Recovery*, that runs `Insomnia.exe
--backstop` at sign-in and every minute (on battery too). It leaves a valid,
unexpired session alone. Once the deadline has passed — say the app crashed or
was killed — it undoes everything in the journal, then deletes itself. The
app, the task and uninstall share one lock file so they never undo over one
another. An unreadable journal is kept as evidence and blocks every change
until you fix or move it; an unreadable session file counts as ended and is
renamed aside, never deleted.

What recovery can't do: the battery floor and the hotspot failover need the
app running. If an app was journaled but Insomnia died in the instant before
confirming the suspend, it isn't resumed blindly — you get a warning naming
the pid; Resource Monitor (`resmon`) → right-click → *Resume Process* fixes it.

### Battery floors

On battery, below 40% → Energy Saver on; below 10% → the session ends (0 turns
the end off; the end floor always stays below the Energy Saver floor). A
battery that can't be read twice in a row while on battery also ends the
session. A desktop has no battery and no floors.

### Hotspot failover

Pick a **saved Wi-Fi profile** in Settings → Network. Connect to your phone's
hotspot once by hand with "Connect automatically" ticked so Windows stores the
password — Insomnia never sees or stores it. When the network path drops for
more than 5 s during a session, Insomnia runs `netsh wlan connect` for that
profile, backing off from 5 s to every 30 s. Outages are logged to
`Logs\handoffs.log`; after one longer than 90 s, tmux panes (inside WSL) that
you marked with `tmux set-option -p -t <target> @insomnia-nudge on` get
`continue` typed into them.

### Browsers

Chromium browsers throttle windows Windows reports as hidden, which can stall
browser-driving agents with the lid shut. If Chrome, Edge, Brave, Vivaldi or
Arc runs without `--disable-backgrounding-occluded-windows`, the tray menu
offers **Relaunch unthrottled**: it closes the browser (after you confirm),
waits up to 10 s, and reopens it with the same profile, both flags and session restore.

## Differences from the macOS app

| macOS | Windows port |
| --- | --- |
| `sudo pmset -a disablesleep 1` via a sudoers rule | power-plan lid action → *Do nothing* + `SetThreadExecutionState`. **No admin, no sudoers.** |
| Low Power Mode (`pmset -b lowpowermode`) | Energy Saver threshold → 100% (also battery-only) |
| SIGSTOP / SIGCONT | `NtSuspendProcess` / `NtResumeProcess` (counted, handle-pinned) |
| launchd agent pinned to the bundle's cdhash | per-user scheduled task, created at session start and self-removing. The exe lives in a user-writable folder like any per-user install; there is no signature pinning |
| Display + keyboard backlight set to 0 via private frameworks | monitors asked to power off (Windows blanks the internal panel on lid close itself). No keyboard-backlight API exists on Windows, and brightness is never written, so nothing can be left dark |
| Thermal rules (serious → Low Power, critical → end) | **not ported** — Windows exposes no thermal state to normal apps |
| App Nap opt-out (`NSAppSleepDisabled`) | EcoQoS opt-out per process; nothing persistent is written |
| CoreWLAN + Keychain hotspot password | saved Windows Wi-Fi profile + `netsh`; Windows keeps the password |
| tmux nudge | same, through `wsl.exe -e tmux` |
| Inline pills that expand in the menu bar | a flyout from the tray eye (the tray can't host inline fields) with the same pills, chips, countdown and hold-to-end |
| — | docked/clamshell detection: lid-close actions skip while an external monitor is in use |
| — | `--start/--extend/--end/--status` command line |

## Files

| Location | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\Programs\Insomnia\Insomnia.exe` | the app |
| `%LOCALAPPDATA%\Insomnia\config.json` | settings (edited through Settings) |
| `%LOCALAPPDATA%\Insomnia\session.json`, `state.json` | the session and the recovery journal |
| `%LOCALAPPDATA%\Insomnia\Logs\insomnia.log`, `handoffs.log` | logs, 1 MiB each with one `.1` kept |
| Task Scheduler → *Insomnia Recovery* | exists only while a session or a pending undo exists |
| `HKCU\…\Run\Insomnia` | launch at sign-in, if chosen |

## Testing without a lid

`scripts\build.ps1 -LidSimulation` builds a variant that accepts
`Insomnia.exe --simulate-lid closed` / `open` and runs the real lid actions. A
normal build has no such command. The core logic has unit tests
(`dotnet test tests/Insomnia.Core.Tests`), run in CI.

## License

MIT, like the original. Original macOS app © krishhgg; see [LICENSE](LICENSE).
