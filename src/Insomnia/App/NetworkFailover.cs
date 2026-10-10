using System.Net.NetworkInformation;
using Insomnia.Core;
using Insomnia.Platform;

namespace Insomnia.App;

/// <summary>
/// Watches the network path. When it drops for more than 5 s during a
/// session, joins the configured hotspot profile, retrying with backoff
/// (5, 10, 20, 30 s, then every 30 s). Every outage is logged to
/// handoffs.log; a long one nudges marked tmux panes once it ends.
/// </summary>
internal sealed class NetworkFailover : IDisposable
{
    readonly SessionManager _m;
    readonly INotifier _notify;
    readonly System.Windows.Forms.Timer _check = new() { Interval = 1000 };
    readonly SynchronizationContext _ui;
    DateTimeOffset? _downSince;
    DateTimeOffset _nextJoin;
    int _attempt;
    bool _joining;
    static readonly int[] Backoff = [5, 10, 20, 30];

    public NetworkFailover(SessionManager m, INotifier notify)
    {
        _m = m;
        _notify = notify;
        _ui = SynchronizationContext.Current!;
        NetworkChange.NetworkAddressChanged += OnChange;
        NetworkChange.NetworkAvailabilityChanged += OnChange;
        _check.Tick += (_, _) => Evaluate();
    }

    void OnChange(object? sender, EventArgs e) => _ui.Post(_ => Evaluate(), null);

    void Evaluate()
    {
        var up = NetworkPath.Satisfied();
        var now = DateTimeOffset.Now;
        if (!up)
        {
            if (_downSince is null)
            {
                _downSince = now;
                _attempt = 0;
                _nextJoin = now.AddSeconds(5);
                _check.Start(); // only ticks during an outage
                Log.Info("network path lost");
            }
            if (_m.IsActive && _m.Config.HotspotProfile.Length > 0 && now >= _nextJoin && !_joining)
                _ = Join(_m.Config.HotspotProfile);
            return;
        }
        if (_downSince is { } since)
        {
            _downSince = null;
            _check.Stop();
            var gap = now - since;
            var text = SessionMath.Gap(gap);
            Log.Handoff($"outage {since:HH:mm:ss} -> {now:HH:mm:ss} ({text}){(_attempt > 0 ? $", {_attempt} join attempt(s)" : "")}");
            Log.Info($"network path back after {text}");
            _m.LastGap = $"{text} at {since:h:mm tt}";
            if (_m.IsActive && gap.TotalSeconds > _m.Config.NudgeThresholdSeconds && gap.TotalSeconds > 5)
                _ = AfterLongOutage(gap);
        }
    }

    async Task Join(string profile)
    {
        _joining = true;
        _attempt++;
        var wait = Backoff[Math.Min(_attempt - 1, Backoff.Length - 1)];
        _nextJoin = DateTimeOffset.Now.AddSeconds(wait);
        var r = await Task.Run(() => NetworkPath.Join(profile));
        Log.Info($"hotspot join attempt {_attempt}: {(r.Ok ? "requested" : (r.Stderr + r.Stdout).Trim())}");
        _joining = false;
    }

    async Task AfterLongOutage(TimeSpan gap)
    {
        var cfg = _m.Config;
        var (nudged, notes) = cfg.TmuxTargets.Count > 0
            ? await Task.Run(() => TmuxNudge.Run(cfg.TmuxTargets, cfg.TmuxNudgePressesEnter))
            : (0, new List<string>());
        foreach (var n in notes) Log.Info($"tmux nudge: {n}");
        var body = $"Network was down {SessionMath.Gap(gap)}.";
        if (cfg.TmuxTargets.Count > 0) body += $" Nudged {nudged} tmux pane{(nudged == 1 ? "" : "s")}.";
        body += " Check GUI agents.";
        _notify.Post("Network back", body);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnChange;
        NetworkChange.NetworkAvailabilityChanged -= OnChange;
        _check.Dispose();
    }
}
