using Bazaar.Server.Browser.Abstractions;
using Bazaar.Server.Browser.Tests.Fakes;

namespace Bazaar.Server.Browser.Tests;

public sealed class BrowserLauncherTests
{
    private const string _agent = "agent-1";

    private readonly FakeProfileDirectory _profiles = new();
    private readonly FakeCookieSource _cookies = new();
    private readonly FakeEgress _egress = new();
    private readonly FakeTaintGate _taint = new();
    private readonly FakeGuestHost _host = new();
    private readonly BrowserLauncher _launcher;

    public BrowserLauncherTests() =>
        _launcher = new BrowserLauncher(_profiles, _cookies, _egress, _taint, _host, new BrowserOptions(), TimeProvider.System);

    private Guid AddProfile(BrowserProfileClass cls, string agent = _agent)
    {
        var id = Guid.NewGuid();
        _profiles.Profiles[id] = new BrowserProfileRef(id, agent, cls, ["example.com"]);
        return id;
    }

    private static BrowserLaunchRequest Req(Guid profile, string agent = _agent, BrowserGuestOs os = BrowserGuestOs.Linux, string? url = "https://example.com/") =>
        new(agent, profile, os, BrowserDisplayMode.Headless, url);

    private static async Task<BrowserLaunchException> Refused(Task<BrowserSession> launch) =>
        await Assert.ThrowsAsync<BrowserLaunchException>(() => launch);

    [Fact]
    public async Task Missing_profile_is_refused()
    {
        var ex = await Refused(_launcher.LaunchAsync(Req(Guid.NewGuid()), TestContext.Current.CancellationToken));
        Assert.Equal(BrowserLaunchFailure.ProfileNotFound, ex.Failure);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task Profile_of_another_agent_is_refused()
    {
        var id = AddProfile(BrowserProfileClass.Account, "someone-else");
        var ex = await Refused(_launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken));
        Assert.Equal(BrowserLaunchFailure.ProfileOwnedByAnotherAgent, ex.Failure);
        Assert.Empty(_host.Started);
        Assert.Empty(_cookies.Fetched);
    }

    [Theory]
    [InlineData(BrowserProfileClass.Anonymous)]
    [InlineData(BrowserProfileClass.Account)]
    public async Task Hard_tainted_agent_gets_no_browser(BrowserProfileClass cls)
    {
        var id = AddProfile(cls);
        _taint.Hard.Add(_agent);
        var ex = await Refused(_launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken));
        Assert.Equal(BrowserLaunchFailure.HardTainted, ex.Failure);
        Assert.Empty(_host.Started);
        Assert.Empty(_cookies.Fetched);
    }

    [Fact]
    public async Task Missing_egress_is_refused()
    {
        var id = AddProfile(BrowserProfileClass.Anonymous);
        _egress.Endpoint = null;
        var ex = await Refused(_launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken));
        Assert.Equal(BrowserLaunchFailure.NoEgress, ex.Failure);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public async Task Guest_start_failure_keeps_inner_exception()
    {
        var id = AddProfile(BrowserProfileClass.Anonymous);
        var inner = new InvalidOperationException("vm down");
        _host.StartFails = inner;
        var ex = await Refused(_launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken));
        Assert.Equal(BrowserLaunchFailure.GuestFailed, ex.Failure);
        Assert.Same(inner, ex.InnerException);
        Assert.Empty(_launcher.ActiveSessions(_agent));
    }

    [Fact]
    public async Task Anonymous_launch_never_touches_cookies_and_passes_start_url()
    {
        var id = AddProfile(BrowserProfileClass.Anonymous);
        var session = await _launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken);

        Assert.Equal(BrowserProfileClass.Anonymous, session.Class);
        Assert.Empty(_cookies.Fetched);
        Assert.Empty(_host.CookieSets);
        var spec = Assert.Single(_host.Started);
        Assert.Contains("https://example.com/", spec.Arguments);
        Assert.Equal(session.SessionId, spec.SessionId);
        Assert.Equal("/usr/bin/chromium", spec.ExecutablePath);
        Assert.Contains(spec.Arguments, a => a.Contains($"/var/lib/bazaar/browser/{id:D}", StringComparison.Ordinal));
        Assert.Single(_launcher.ActiveSessions(_agent));
    }

    [Fact]
    public async Task Windows_guest_uses_windows_paths()
    {
        var id = AddProfile(BrowserProfileClass.Anonymous);
        await _launcher.LaunchAsync(Req(id, os: BrowserGuestOs.Windows), TestContext.Current.CancellationToken);
        var spec = Assert.Single(_host.Started);
        Assert.Equal(new BrowserOptions().WindowsExecutable, spec.ExecutablePath);
        Assert.Contains(spec.Arguments, a => a.Contains($@"C:\ProgramData\Bazaar\browser\{id:D}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Account_cookies_are_set_before_launch_returns_and_start_url_is_withheld()
    {
        var id = AddProfile(BrowserProfileClass.Account);
        var session = await _launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken);

        Assert.Equal(["start", "cookies"], _host.Events);
        Assert.Equal(session.SessionId, Assert.Single(_cookies.Fetched));
        var (_, set) = Assert.Single(_host.CookieSets);
        Assert.Equal("sid", Assert.Single(set).Name);
        Assert.DoesNotContain(_host.Started[0].Arguments, a => a.Contains("example.com/", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cookie_failure_rolls_back_without_leaking_values(bool fetchFails)
    {
        var id = AddProfile(BrowserProfileClass.Account);
        var failure = new InvalidOperationException("boom");
        if (fetchFails) _cookies.FailWith = failure; else _host.SetCookiesFails = failure;

        var ex = await Refused(_launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken));

        Assert.Equal(BrowserLaunchFailure.CookiesRefused, ex.Failure);
        Assert.Same(failure, ex.InnerException);
        Assert.DoesNotContain("SECRET-VALUE", ex.ToString(), StringComparison.Ordinal);
        Assert.Single(_host.Stopped);
        Assert.Single(_cookies.Ended);
        Assert.Empty(_launcher.ActiveSessions(_agent));
    }

    [Fact]
    public async Task EndAsync_stops_ends_cookies_and_is_idempotent()
    {
        var id = AddProfile(BrowserProfileClass.Account);
        var session = await _launcher.LaunchAsync(Req(id), TestContext.Current.CancellationToken);

        await _launcher.EndAsync(session.SessionId, TestContext.Current.CancellationToken);
        await _launcher.EndAsync(session.SessionId, TestContext.Current.CancellationToken);
        await _launcher.EndAsync("unknown", TestContext.Current.CancellationToken);

        Assert.Single(_host.Stopped);
        Assert.Equal([session.SessionId], _cookies.Ended);
        Assert.Empty(_launcher.ActiveSessions(_agent));
    }

    [Fact]
    public async Task Detach_ends_only_that_agents_account_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var acct1 = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Account)), ct);
        var acct2 = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Account)), ct);
        var anon = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Anonymous)), ct);
        var otherAcct = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Account, "agent-2"), "agent-2"), ct);

        var count = await _launcher.DetachAccountSessionsAsync(_agent, ct);

        Assert.Equal(2, count);
        Assert.Equal([anon.SessionId], _launcher.ActiveSessions(_agent).Select(s => s.SessionId));
        Assert.Single(_launcher.ActiveSessions("agent-2"));
        Assert.Contains(acct1.SessionId, _cookies.Ended);
        Assert.Contains(acct2.SessionId, _cookies.Ended);
        Assert.DoesNotContain(otherAcct.SessionId, _cookies.Ended);
        Assert.DoesNotContain(anon.SessionId, _cookies.Ended);
        Assert.Equal(0, await _launcher.DetachAccountSessionsAsync(_agent, ct));
    }

    [Fact]
    public async Task Detach_continues_after_a_stop_failure_then_throws_aggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Account)), ct);
        var second = await _launcher.LaunchAsync(Req(AddProfile(BrowserProfileClass.Account)), ct);
        _host.StopFails = h => h.SessionId == first.SessionId ? new IOException("stuck") : null;

        var ex = await Assert.ThrowsAsync<AggregateException>(() => _launcher.DetachAccountSessionsAsync(_agent, ct));

        Assert.Single(ex.InnerExceptions);
        Assert.Equal(2, _host.Stopped.Count);
        Assert.Contains(second.SessionId, _cookies.Ended);
        Assert.Contains(first.SessionId, _cookies.Ended);
        Assert.Empty(_launcher.ActiveSessions(_agent));
    }

    [Fact]
    public async Task Concurrent_launches_get_unique_sessions()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = Enumerable.Range(0, 3).Select(_ => AddProfile(BrowserProfileClass.Account)).ToArray();
        var tasks = Enumerable.Range(0, 60).Select(i => Task.Run(() => _launcher.LaunchAsync(Req(ids[i % 3]), ct), ct));

        var sessions = await Task.WhenAll(tasks);

        Assert.Equal(60, sessions.Select(s => s.SessionId).Distinct().Count());
        Assert.Equal(60, _launcher.ActiveSessions(_agent).Count);
        Assert.Equal(60, await _launcher.DetachAccountSessionsAsync(_agent, ct));
    }
}
