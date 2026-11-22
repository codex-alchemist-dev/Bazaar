using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Browser.Tests.Fakes;

namespace Bazaar.Server.Browser.Tests;

public sealed class BrowserSessionSweeperTests
{
    [Fact]
    public async Task Sweep_ends_only_sessions_older_than_max_age()
    {
        var time = new ManualTime(DateTimeOffset.Parse("2026-11-19T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var profiles = new FakeProfileDirectory();
        var id = Guid.NewGuid();
        profiles.Profiles[id] = new BrowserProfileRef(id, "a", BrowserProfileClass.Anonymous, []);
        var options = new BrowserOptions(MaxSessionAge: TimeSpan.FromHours(1));
        var launcher = new BrowserLauncher(profiles, new FakeCookieSource(), new FakeEgress(), new FakeTaintGate(), new FakeGuestHost(), options, time);
        var req = new BrowserLaunchRequest("a", id, BrowserGuestOs.Linux, BrowserDisplayMode.Headless, null);

        var old = await launcher.LaunchAsync(req, CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(90));
        var fresh = await launcher.LaunchAsync(req, CancellationToken.None);

        var ended = await new BrowserSessionSweeper(launcher, options, time).SweepAsync(CancellationToken.None);

        Assert.Equal(1, ended);
        var left = launcher.AllActiveSessions();
        Assert.Single(left);
        Assert.Equal(fresh.SessionId, left[0].SessionId);
        Assert.DoesNotContain(left, s => s.SessionId == old.SessionId);
    }

    [Fact]
    public void Max_age_defaults_to_eight_hours_when_unset_or_not_positive()
    {
        Assert.Equal(TimeSpan.FromHours(8), new BrowserOptions().EffectiveMaxSessionAge);
        Assert.Equal(TimeSpan.FromHours(8), new BrowserOptions(MaxSessionAge: TimeSpan.FromSeconds(-1)).EffectiveMaxSessionAge);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
