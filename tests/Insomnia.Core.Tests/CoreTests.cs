using Insomnia.Core;
using Xunit;

namespace Insomnia.Core.Tests;

public class SessionMathTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StartClampsToThirtyDays()
    {
        var s = SessionMath.Start(T0, TimeSpan.FromDays(45))!;
        Assert.Equal(TimeSpan.FromDays(30), s.EndsAt - s.StartedAt);
    }

    [Fact]
    public void StartRefusesZero() => Assert.Null(SessionMath.Start(T0, TimeSpan.Zero));

    [Fact]
    public void ExtendNeverPassesTheCeiling()
    {
        var s = SessionMath.Start(T0, TimeSpan.FromDays(29))!;
        var added = SessionMath.Extend(s, T0, TimeSpan.FromDays(3));
        Assert.Equal(24 * 60, added);
        Assert.Equal(T0 + TimeSpan.FromDays(30), s.EndsAt);
        Assert.Equal(0, SessionMath.Extend(s, T0, TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData("90", 90)]
    [InlineData("90m", 90)]
    [InlineData("4h", 240)]
    [InlineData("1d2h30m", 1590)]
    [InlineData(" 2H ", 120)]
    public void ParsesDurations(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), SessionMath.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("h")]
    [InlineData("4x")]
    [InlineData("4h3")]
    public void RejectsBadDurations(string text) => Assert.Null(SessionMath.ParseDuration(text));

    [Theory]
    [InlineData(65, "1:05")]
    [InlineData(3 * 3600 + 4 * 60 + 5, "3:04:05")]
    [InlineData(2 * 86400 + 3 * 3600, "2d 03:00:00")]
    public void Countdown(int seconds, string expected) =>
        Assert.Equal(expected, SessionMath.Countdown(TimeSpan.FromSeconds(seconds)));
}

public class DurationInputTests
{
    [Fact]
    public void EmptyMeansDefault() => Assert.Equal(DurationInput.Outcome.Empty, DurationInput.Read("", "", "").outcome);

    [Fact]
    public void CombinesPills() =>
        Assert.Equal(TimeSpan.FromMinutes(1 * 1440 + 2 * 60 + 3), DurationInput.Read("1", "2", "3").duration);

    [Fact]
    public void RefusesOverThirtyDays() => Assert.Equal(DurationInput.Outcome.TooLong, DurationInput.Read("30", "1", "").outcome);

    [Fact]
    public void ZeroIsZero() => Assert.Equal(DurationInput.Outcome.Zero, DurationInput.Read("0", "", "0").outcome);

    [Fact]
    public void SanitizeKeepsFourDigits() => Assert.Equal("1234", DurationInput.Sanitize("1a2b345"));
}

public class FloorRulesTests
{
    static readonly Config C = new();

    [Fact]
    public void BelowEndFloorOnBatteryEnds() =>
        Assert.Equal(new FloorAction.EndSession(EndReason.BatteryFloor),
            Assert.Single(FloorRules.Evaluate(new BatteryStatus(true, 5, false, 0), false, false, C)));

    [Fact]
    public void ChargingNeverEnds() =>
        Assert.Empty(FloorRules.Evaluate(new BatteryStatus(true, 5, true, 0), false, false, C));

    [Fact]
    public void EndFloorZeroTurnsTheEndOff()
    {
        var c = new Config { EndFloor = 0 };
        Assert.IsType<FloorAction.EnableLowPower>(Assert.Single(FloorRules.Evaluate(new BatteryStatus(true, 1, false, 0), false, false, c)));
    }

    [Fact]
    public void UnreadableTwiceOnBatteryEnds()
    {
        Assert.Empty(FloorRules.Evaluate(new BatteryStatus(true, null, false, 1), false, false, C));
        Assert.Equal(new FloorAction.EndSession(EndReason.BatteryUnreadable),
            Assert.Single(FloorRules.Evaluate(new BatteryStatus(true, null, false, 2), false, false, C)));
    }

    [Fact]
    public void DesktopHasNoFloor() =>
        Assert.Empty(FloorRules.Evaluate(BatteryStatus.NoBattery, false, false, C));

    [Fact]
    public void LidClosedWantsEnergySaver() =>
        Assert.Equal(new FloorAction.EnableLowPower(LowPowerCause.Lid),
            Assert.Single(FloorRules.Evaluate(new BatteryStatus(true, 80, false, 0), true, false, C)));

    [Fact]
    public void NoCauseTurnsOursOff() =>
        Assert.IsType<FloorAction.DisableLowPower>(Assert.Single(FloorRules.Evaluate(new BatteryStatus(true, 80, true, 0), false, true, C)));

    [Fact]
    public void FloorsStayOrdered()
    {
        var c = new Config { LowPowerFloor = 10, EndFloor = 30 };
        Assert.NotNull(c.NormalizeFloors());
        Assert.True(c.EndFloor < c.LowPowerFloor);
        c.SetEndFloor(60);
        Assert.Equal(65, c.LowPowerFloor);
        c.SetLowPowerFloor(20);
        Assert.Equal(15, c.EndFloor);
    }
}

public class FreezePlannerTests
{
    static FreezePlanner.Candidate C(int pid, string name, bool window = true, bool same = true) => new(pid, name, window, same);

    [Fact]
    public void FreezeListTakesEveryProcessOfTheApp()
    {
        var plan = FreezePlanner.Plan([C(10, "slack.exe"), C(11, "slack.exe", window: false), C(12, "Code.exe")], new Config(), 1);
        Assert.Equal([10, 11], plan.Select(p => p.Pid).Order().ToArray());
    }

    [Fact]
    public void FreezeAllSkipsProtectedAgentsAndMeetings()
    {
        var c = new Config { FreezeAllApps = true };
        var plan = FreezePlanner.Plan(
            [C(10, "Spotify.exe"), C(11, "Code.exe"), C(12, "Zoom.exe"), C(13, "explorer.exe"), C(14, "helper.exe", window: false)], c, 1);
        Assert.Equal(["Spotify.exe"], plan.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ExplicitListBeatsProtection()
    {
        var c = new Config { FreezeList = ["Zoom"] };
        Assert.Single(FreezePlanner.Plan([C(12, "Zoom.exe")], c, 1));
    }

    [Fact]
    public void NeverFreezesItselfOrOtherSessions()
    {
        var c = new Config { FreezeList = ["Insomnia.exe", "slack.exe"] };
        var plan = FreezePlanner.Plan([C(1, "slack.exe"), C(20, "Insomnia.exe"), C(21, "slack.exe", same: false)], c, 1);
        Assert.Empty(plan);
    }
}

public class StoreTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "insomnia-test-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void RoundTripsStateAndSession()
    {
        var store = new Store(new Paths(_dir));
        var st = new RuntimeState();
        st.LidActionOverrides.Add(new LidActionOverride { Scheme = Guid.NewGuid(), PreviousAc = 1, PreviousDc = 1 });
        st.FrozenProcesses.Add(new FrozenProcess { Pid = 42, Name = "slack.exe", CreationFileTime = 123, Confirmed = true });
        store.SaveState(st);
        var back = store.LoadState();
        Assert.True(back.SleepDisabledByUs);
        Assert.Equal(42, back.FrozenProcesses[0].Pid);

        var s = SessionMath.Start(DateTimeOffset.Now, TimeSpan.FromHours(1))!;
        store.SaveSession(s);
        Assert.Equal(JsonStore.ReadResult.Ok, store.LoadSession(out var s2));
        Assert.Equal(s.EndsAt, s2!.EndsAt);
    }

    [Fact]
    public void UnreadableJournalRefuses()
    {
        var store = new Store(new Paths(_dir));
        File.WriteAllText(store.Paths.State, "{not json");
        Assert.Throws<UnreadableJournalException>(() => store.LoadState());
    }

    [Fact]
    public void UnreadableSessionIsNotMissing()
    {
        var store = new Store(new Paths(_dir));
        File.WriteAllText(store.Paths.Session, "garbage");
        Assert.Equal(JsonStore.ReadResult.Unreadable, store.LoadSession(out _));
        var aside = Store.SetAside(store.Paths.Session);
        Assert.True(File.Exists(aside));
        Assert.False(File.Exists(store.Paths.Session));
    }

    [Fact]
    public void ConfigFloorsCorrectedAtLoad()
    {
        var store = new Store(new Paths(_dir));
        File.WriteAllText(store.Paths.Config, """{"lowPowerFloor": 10, "endFloor": 20}""");
        var c = store.LoadConfig(out var note);
        Assert.NotNull(note);
        Assert.Equal(25, c.LowPowerFloor);
    }

    [Fact]
    public void LockIsExclusive()
    {
        var paths = new Paths(_dir);
        using var a = RecoveryLock.TryAcquire(paths, TimeSpan.Zero);
        Assert.NotNull(a);
        Assert.Null(RecoveryLock.TryAcquire(paths, TimeSpan.FromMilliseconds(100)));
    }
}
