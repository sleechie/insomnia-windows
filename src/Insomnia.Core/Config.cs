using System.Text.Json.Serialization;

namespace Insomnia.Core;

/// <summary>
/// User settings, persisted as <c>config.json</c>. Missing keys load with the
/// defaults below, so a config written by an older build still reads.
/// Apps are identified by executable name (for example <c>slack.exe</c>),
/// matched case-insensitively — the Windows counterpart of a macOS bundle id.
/// </summary>
public sealed class Config
{
    // Session
    /// <summary>Preset durations in minutes, offered in the right-click menu.</summary>
    public List<int> PresetsMinutes { get; set; } = [30, 60, 120, 240, 480, 720, 1440, 4320];
    /// <summary>What an Enter with every field empty starts, in minutes.</summary>
    public int DefaultPresetMinutes { get; set; } = 240;

    // Lid-close actions
    /// <summary>Apps suspended while the lid is closed.</summary>
    public List<string> FreezeList { get; set; } = [.. Defaults.FreezeList];
    /// <summary>Also suspend every other windowed app that is not an agent app or built-in protected.</summary>
    public bool FreezeAllApps { get; set; }
    /// <summary>Suspend Docker Desktop on lid close when <c>docker ps -q</c> finds nothing running (asked twice).</summary>
    public bool DockerRule { get; set; }
    /// <summary>Mute the default output on lid close; restore it on open.</summary>
    public bool MuteOnLidClose { get; set; } = true;
    /// <summary>Ask every monitor to power down on lid close (an external one stays lit otherwise).</summary>
    public bool DisplayOffOnLidClose { get; set; } = true;
    /// <summary>Turn Energy Saver on while the lid is closed.</summary>
    public bool EnergySaverOnLidClose { get; set; } = true;
    /// <summary>
    /// Closing the lid while an external monitor is in use is "clamshell"
    /// desk use, not walking away: the session still keeps the machine
    /// awake, but nothing is frozen, muted or blanked.
    /// </summary>
    public bool SkipLidActionsWithExternalDisplay { get; set; } = true;

    // Agent apps
    /// <summary>Apps that are never frozen by the automatic scope.</summary>
    public List<string> AgentList { get; set; } = [.. Defaults.AgentList];
    /// <summary>Opt agent processes out of Windows power throttling (EcoQoS) for the session.</summary>
    public bool DisablePowerThrottlingForAgents { get; set; }

    // Battery floors
    /// <summary>Battery percentage below which Energy Saver is switched on.</summary>
    public int LowPowerFloor { get; set; } = 40;
    /// <summary>Battery percentage below which the session ends. 0 turns the battery end off.</summary>
    public int EndFloor { get; set; } = 10;

    // Network failover
    /// <summary>Name of a saved Windows Wi-Fi profile (usually the SSID) to join when the network drops.</summary>
    public string HotspotProfile { get; set; } = "";
    /// <summary>Seconds of outage after which tagged tmux panes are nudged.</summary>
    public int NudgeThresholdSeconds { get; set; } = 90;
    /// <summary>tmux targets (session:window.pane) inside WSL.</summary>
    public List<string> TmuxTargets { get; set; } = [];
    public bool TmuxNudgePressesEnter { get; set; }

    // App
    public bool LaunchAtLogin { get; set; }

    public const int MaxDurationMinutes = 30 * 24 * 60;

    /// <summary>
    /// Keeps a non-zero end floor below the Energy Saver floor so the mode
    /// gets a chance to come on before the session ends. Returns a log line
    /// when something changed.
    /// </summary>
    public string? NormalizeFloors()
    {
        var before = (LowPowerFloor, EndFloor);
        LowPowerFloor = Math.Clamp(LowPowerFloor, 0, 100);
        EndFloor = Math.Clamp(EndFloor, 0, 95);
        if (EndFloor > 0 && EndFloor >= LowPowerFloor)
            LowPowerFloor = Math.Min(100, EndFloor + 5);
        if (DefaultPresetMinutes <= 0 || DefaultPresetMinutes > MaxDurationMinutes) DefaultPresetMinutes = 240;
        PresetsMinutes = PresetsMinutes.Where(p => p > 0 && p <= MaxDurationMinutes).Distinct().Order().ToList();
        if (NudgeThresholdSeconds < 0) NudgeThresholdSeconds = 90;
        return before == (LowPowerFloor, EndFloor)
            ? null
            : $"battery floors corrected: lowPowerFloor {before.LowPowerFloor} -> {LowPowerFloor}, endFloor {before.EndFloor} -> {EndFloor}";
    }

    /// <summary>Settings stepper: moving the end floor drags the Energy Saver floor up with it.</summary>
    public void SetEndFloor(int value)
    {
        EndFloor = Math.Clamp(value, 0, 95);
        if (EndFloor > 0 && EndFloor >= LowPowerFloor) LowPowerFloor = Math.Min(100, EndFloor + 5);
    }

    /// <summary>Settings stepper: moving the Energy Saver floor drags the end floor down with it.</summary>
    public void SetLowPowerFloor(int value)
    {
        LowPowerFloor = Math.Clamp(value, 5, 100);
        if (EndFloor > 0 && EndFloor >= LowPowerFloor) EndFloor = Math.Max(0, LowPowerFloor - 5);
    }

    public Config Clone() => JsonStore.Deserialize<Config>(JsonStore.Serialize(this))!;
}

public static class Defaults
{
    public static readonly string[] FreezeList = ["slack.exe", "WhatsApp.exe", "Discord.exe"];

    /// <summary>Agent hosts that should keep running at full speed with the lid shut.</summary>
    public static readonly string[] AgentList =
    [
        "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "wsl.exe", "wslhost.exe", "wslservice.exe", "vmmem", "vmmemWSL",
        "WezTerm-gui.exe", "Warp.exe", "alacritty.exe",
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "Arc.exe", "vivaldi.exe",
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Zed.exe", "Windsurf.exe", "Antigravity.exe",
        "claude.exe", "Claude.exe", "ChatGPT.exe", "Codex.exe", "node.exe", "python.exe", "pythonw.exe",
        "Docker Desktop.exe", "com.docker.backend.exe",
        "tailscale-ipn.exe", "tailscaled.exe", "LM Studio.exe", "ollama.exe", "ollama app.exe",
    ];

    /// <summary>
    /// Never picked up by "freeze every other app", even on a config that
    /// predates them: editors, terminals, browsers, AI apps, password
    /// managers, local databases, VPNs, model servers, and the Windows shell.
    /// Only an explicit freeze-list entry overrides this.
    /// </summary>
    public static readonly string[] BuiltInProtected =
    [
        // Windows shell and system UI
        "explorer.exe", "dwm.exe", "csrss.exe", "winlogon.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe",
        "SearchHost.exe", "SearchApp.exe", "TextInputHost.exe", "ApplicationFrameHost.exe", "SystemSettings.exe",
        "Taskmgr.exe", "LockApp.exe", "sihost.exe", "ctfmon.exe", "SecurityHealthSystray.exe", "PhoneExperienceHost.exe",
        "Widgets.exe", "WidgetService.exe", "RuntimeBroker.exe", "smartscreen.exe", "mmc.exe", "regedit.exe",
        // Terminals and shells
        "WindowsTerminal.exe", "wt.exe", "OpenConsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "powershell_ise.exe", "wsl.exe", "wslhost.exe", "WezTerm-gui.exe", "Warp.exe", "alacritty.exe", "mintty.exe",
        "putty.exe", "MobaXterm.exe", "Tabby.exe", "Hyper.exe",
        // Editors and IDEs
        "Code.exe", "Code - Insiders.exe", "Cursor.exe", "Zed.exe", "Windsurf.exe", "Antigravity.exe", "devenv.exe",
        "sublime_text.exe", "notepad++.exe", "notepad.exe", "Notepad.exe", "nvim-qt.exe", "gvim.exe", "rider64.exe",
        "idea64.exe", "pycharm64.exe", "webstorm64.exe", "goland64.exe", "clion64.exe", "phpstorm64.exe",
        "rubymine64.exe", "datagrip64.exe", "studio64.exe", "fleet.exe",
        // Browsers
        "chrome.exe", "msedge.exe", "brave.exe", "firefox.exe", "Arc.exe", "vivaldi.exe", "opera.exe", "chromium.exe",
        // AI apps and local model servers
        "claude.exe", "Claude.exe", "ChatGPT.exe", "Codex.exe", "LM Studio.exe", "ollama.exe", "ollama app.exe",
        "Jan.exe", "GPT4All.exe",
        // Password managers
        "1Password.exe", "Bitwarden.exe", "KeePass.exe", "KeePassXC.exe", "Dashlane.exe", "LastPass.exe", "Proton Pass.exe",
        // Local databases and dev infrastructure
        "postgres.exe", "pg_ctl.exe", "mysqld.exe", "mongod.exe", "redis-server.exe", "TablePlus.exe", "DBeaver.exe",
        "Docker Desktop.exe", "com.docker.backend.exe", "Rancher Desktop.exe", "podman-desktop.exe",
        // VPN and remote access
        "tailscale-ipn.exe", "tailscaled.exe", "openvpn-gui.exe", "WireGuard.exe", "RustDesk.exe", "mstsc.exe",
        // Insomnia itself
        "Insomnia.exe",
    ];

    /// <summary>
    /// Meeting, recording and dictation apps (and their helpers). Freezing one
    /// ends the call or the recording, so "freeze every other app" never
    /// touches them. A freeze-list entry by hand still does.
    /// </summary>
    public static readonly string[] MeetingApps =
    [
        "Zoom.exe", "ZoomIt.exe", "CptHost.exe", "ms-teams.exe", "Teams.exe", "msteams.exe", "msedgewebview2.exe",
        "CiscoCollabHost.exe", "webexmta.exe", "atmgr.exe", "Webex.exe", "Wispr Flow.exe", "Granola.exe", "Otter.exe",
        "obs64.exe", "obs32.exe", "Loom.exe", "Skype.exe",
        "Audacity.exe", "Krisp.exe", "NVIDIA Broadcast.exe",
    ];
}
