using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

public class AnonymousProfileTests
{
    [Fact]
    public async Task Anonymous_profile_holds_no_cookies()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(BrowserProfileTestKit.AgentId, BrowserProfileKind.Anonymous, [], TestContext.Current.CancellationToken);

        var jar = await store.ReadCookiesAsync(profile.ProfileId, TestContext.Current.CancellationToken);

        Assert.Equal(0, jar.Count);
        Assert.Empty(jar.Cookies);
    }

    [Fact]
    public async Task Writing_a_cookie_into_an_anonymous_profile_throws()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(BrowserProfileTestKit.AgentId, BrowserProfileKind.Anonymous, [], TestContext.Current.CancellationToken);
        var jar = new CookieJar([BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA)]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteCookiesAsync(profile.ProfileId, jar, TestContext.Current.CancellationToken));

        Assert.Equal(0, (await store.ReadCookiesAsync(profile.ProfileId, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Anonymous_profile_takes_no_allowlist()
    {
        var store = BrowserProfileTestKit.NewStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Anonymous, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Injection_is_refused_for_an_anonymous_profile()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(BrowserProfileTestKit.AgentId, BrowserProfileKind.Anonymous, [], TestContext.Current.CancellationToken);
        var injector = new CookieInjector(store, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => injector.InjectAsync(
            BrowserProfileTestKit.Lease(BrowserProfileTestKit.ServiceA),
            BrowserProfileTestKit.ServiceA,
            profile,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Materialisation_is_refused_for_an_anonymous_profile()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(BrowserProfileTestKit.AgentId, BrowserProfileKind.Anonymous, [], TestContext.Current.CancellationToken);
        var materializer = new CookieProfileMaterializer(store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            materializer.MaterializeAsync("session-1", profile.ProfileId, TestContext.Current.CancellationToken));
    }
}
