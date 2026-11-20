using Bazaar.Server.Browser.Abstractions;

namespace Bazaar.Server.Browser.Tests.Fakes;

internal sealed class FakeProfileDirectory : IBrowserProfileDirectory
{
    public Dictionary<Guid, BrowserProfileRef> Profiles { get; } = [];

    public Task<BrowserProfileRef?> FindAsync(Guid profileId, CancellationToken ct) =>
        Task.FromResult(Profiles.GetValueOrDefault(profileId));
}

internal sealed class FakeCookieSource : IBrowserCookieSource
{
    private readonly Lock _gate = new();
    public List<string> Fetched { get; } = [];
    public List<string> Ended { get; } = [];
    public Exception? FailWith { get; set; }
    public IReadOnlyList<BrowserCookieEntry> Cookies { get; set; } = [new("sid", "SECRET-VALUE", "example.com", "/", null)];

    public Task<IReadOnlyList<BrowserCookieEntry>> GetCookiesAsync(string sessionId, BrowserProfileRef profile, CancellationToken ct)
    {
        lock (_gate)
            Fetched.Add(sessionId);
        if (FailWith is not null)
            throw FailWith;
        return Task.FromResult(Cookies);
    }

    public void EndSession(string sessionId)
    {
        lock (_gate)
            Ended.Add(sessionId);
    }
}

internal sealed class FakeEgress : IBrowserEgressPolicy
{
    public BrowserEgressEndpoint? Endpoint { get; set; } = new("10.0.2.2", 3128);

    public Task<BrowserEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct) => Task.FromResult(Endpoint);
}

internal sealed class FakeTaintGate : IBrowserTaintGate
{
    public HashSet<string> Hard { get; } = [];

    public bool IsHardTainted(string agentId) => Hard.Contains(agentId);
}

internal sealed class FakeGuestHost : IBrowserGuestHost
{
    private readonly Lock _gate = new();
    private int _next;
    public List<BrowserLaunchSpec> Started { get; } = [];
    public List<(BrowserProcessHandle Handle, IReadOnlyList<BrowserCookieEntry> Cookies)> CookieSets { get; } = [];
    public List<BrowserProcessHandle> Stopped { get; } = [];
    public List<string> Events { get; } = [];
    public Exception? StartFails { get; set; }
    public Exception? SetCookiesFails { get; set; }
    public Func<BrowserProcessHandle, Exception?>? StopFails { get; set; }

    public Task<BrowserProcessHandle> StartAsync(BrowserLaunchSpec spec, CancellationToken ct)
    {
        if (StartFails is not null)
            throw StartFails;
        lock (_gate)
        {
            Started.Add(spec);
            Events.Add("start");
            return Task.FromResult(new BrowserProcessHandle(spec.SessionId, "p" + _next++));
        }
    }

    public Task SetCookiesAsync(BrowserProcessHandle handle, IReadOnlyList<BrowserCookieEntry> cookies, CancellationToken ct)
    {
        if (SetCookiesFails is not null)
            throw SetCookiesFails;
        lock (_gate)
        {
            CookieSets.Add((handle, cookies));
            Events.Add("cookies");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(BrowserProcessHandle handle, CancellationToken ct)
    {
        lock (_gate)
        {
            Stopped.Add(handle);
            Events.Add("stop");
        }

        var ex = StopFails?.Invoke(handle);
        return ex is null ? Task.CompletedTask : Task.FromException(ex);
    }
}
