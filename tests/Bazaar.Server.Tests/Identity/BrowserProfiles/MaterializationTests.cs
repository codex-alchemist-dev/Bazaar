using Bazaar.Server.Identity.BrowserProfiles;

namespace Bazaar.Server.Tests.Identity.BrowserProfiles;

public class MaterializationTests
{
    [Fact]
    public async Task Materialisation_returns_only_allowlisted_cookies()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken);
        await store.WriteCookiesAsync(profile.ProfileId, new CookieJar(
        [
            BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA, "kept"),
        ]), TestContext.Current.CancellationToken);
        var materializer = new CookieProfileMaterializer(store);

        var session = await materializer.MaterializeAsync("session-1", profile.ProfileId, TestContext.Current.CancellationToken);

        Assert.True(session.IsActive);
        Assert.Equal(["kept"], session.Cookies.Select(c => c.Name));
    }

    [Fact]
    public async Task Materialisation_ends_after_EndSession()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken);
        await store.WriteCookiesAsync(profile.ProfileId, new CookieJar(
            [BrowserProfileTestKit.Cookie(BrowserProfileTestKit.ServiceA)]), TestContext.Current.CancellationToken);
        var materializer = new CookieProfileMaterializer(store);
        var session = await materializer.MaterializeAsync("session-1", profile.ProfileId, TestContext.Current.CancellationToken);
        Assert.NotNull(materializer.TryGetActive("session-1"));

        materializer.EndSession("session-1");

        Assert.Null(materializer.TryGetActive("session-1"));
        Assert.False(session.IsActive);
        Assert.Throws<ObjectDisposedException>(() => session.Cookies);
    }

    [Fact]
    public async Task Sessions_are_independent()
    {
        var store = BrowserProfileTestKit.NewStore();
        var profile = await store.CreateAsync(
            BrowserProfileTestKit.AgentId, BrowserProfileKind.Account, [BrowserProfileTestKit.ServiceA], TestContext.Current.CancellationToken);
        var materializer = new CookieProfileMaterializer(store);
        await materializer.MaterializeAsync("session-1", profile.ProfileId, TestContext.Current.CancellationToken);
        await materializer.MaterializeAsync("session-2", profile.ProfileId, TestContext.Current.CancellationToken);

        materializer.EndSession("session-1");

        Assert.Null(materializer.TryGetActive("session-1"));
        Assert.NotNull(materializer.TryGetActive("session-2"));
    }

    [Fact]
    public void EndSession_for_an_unknown_session_is_a_no_op()
    {
        var materializer = new CookieProfileMaterializer(BrowserProfileTestKit.NewStore());

        materializer.EndSession("never-started");

        Assert.Null(materializer.TryGetActive("never-started"));
    }
}
