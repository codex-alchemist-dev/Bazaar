using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

public class InjectionTests
{
    private static async Task<BrowserProfile> AccountWithCookiesAsync(
        InMemoryBrowserProfileStore store,
        params BrowserCookie[] cookies)
    {
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId,
            BrowserProfileKind.Account,
            [BrowserProfileTestKit.ServiceA, BrowserProfileTestKit.ServiceB],
            TestContext.Current.CancellationToken);
        await store.WriteCookiesAsync(profile.ProfileId, new CookieJar(cookies), TestContext.Current.CancellationToken);
        return profile;
    }

    [Fact]
    public async Task Cookie_for_service_A_is_never_returned_for_service_B()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await AccountWithCookiesAsync(
            store,
            BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "only-a"));
        var injector = new CookieInjector(store, TimeProvider.System);

        var forB = await injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceB),
            BrowserProfileTestKit.ServiceB,
            profile,
            TestContext.Current.CancellationToken);

        Assert.Empty(forB);
    }

    [Fact]
    public async Task Injection_returns_only_the_cookies_for_the_lease_service()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await AccountWithCookiesAsync(
            store,
            BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "a-one"),
            BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceB, "b-one"),
            BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "a-two"));
        var injector = new CookieInjector(store, TimeProvider.System);

        var forA = await injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceA),
            BrowserProfileTestKit.ServiceA,
            profile,
            TestContext.Current.CancellationToken);

        Assert.Equal(["a-one", "a-two"], forA.Select(c => c.Name));
    }

    [Fact]
    public async Task Subdomain_service_receives_a_parent_domain_cookie_but_a_lookalike_does_not()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, ["example.test"], TestContext.Current.CancellationToken);
        await store.WriteCookiesAsync(
            profile.ProfileId,
            new CookieJar([BrowserProfileTestKit.Cookie("example.test", "parent")]),
            TestContext.Current.CancellationToken);
        var injector = new CookieInjector(store, TimeProvider.System);

        var sub = await injector.InjectAsync(
            BrowserProfileTestKit.Lease("www.example.test"), "www.example.test", profile, TestContext.Current.CancellationToken);
        var lookalike = await injector.InjectAsync(
            BrowserProfileTestKit.Lease("evil-example.test"), "evil-example.test", profile, TestContext.Current.CancellationToken);

        Assert.Equal(["parent"], sub.Select(c => c.Name));
        Assert.Empty(lookalike);
    }

    [Fact]
    public async Task Expired_cookies_are_not_injected()
    {
        var store = BrowserProfileTestKit.NewStore();
        var expired = new BrowserCookie(
            "old", "stale-value", BrowserProfileTestKit.ServiceA, "/", DateTimeOffset.UtcNow.AddDays(-1));
        var profile = await AccountWithCookiesAsync(store, expired);
        var injector = new CookieInjector(store, TimeProvider.System);

        var result = await injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceA),
            BrowserProfileTestKit.ServiceA,
            profile,
            TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Lease_for_a_different_service_is_refused()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await AccountWithCookiesAsync(store);
        var injector = new CookieInjector(store, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceA),
            BrowserProfileTestKit.ServiceB,
            profile,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lease_for_another_agent_is_refused()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await AccountWithCookiesAsync(store);
        var injector = new CookieInjector(store, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceA, "agent-other"),
            BrowserProfileTestKit.ServiceA,
            profile,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Expired_lease_is_refused()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await AccountWithCookiesAsync(store);
        var injector = new CookieInjector(store, TimeProvider.System);
        var lease = new BrowserLease(
            new Bazaar.Server.Identity.LeaseId(Guid.NewGuid()),
            BrowserProfileTestKit.AgentId,
            BrowserProfileTestKit.ServiceA,
            DateTimeOffset.UtcNow.AddMinutes(-1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => injector.InjectAsync(
            lease, BrowserProfileTestKit.ServiceA, profile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writing_a_cookie_outside_the_allowlist_is_refused()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteCookiesAsync(
            profile.ProfileId,
            new CookieJar([BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceB)]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parent_domain_cookie_is_refused_for_a_narrower_allowlist_entry()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, ["www.example.test"], TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteCookiesAsync(
            profile.ProfileId,
            new CookieJar([BrowserProfileTestKit.Cookie("example.test")]),
            TestContext.Current.CancellationToken));
    }
}
