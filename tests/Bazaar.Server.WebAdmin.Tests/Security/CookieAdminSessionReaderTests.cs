using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.WebAdmin.Tests.Security;

public sealed class CookieAdminSessionReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CookieAdminSessionReader Reader, InMemoryAdminSessionStore Store, FakeTimeProvider Time) Make()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        return (new CookieAdminSessionReader(store, time), store, time);
    }

    private static DefaultHttpContext Request(string? cookieName, string? value, bool https)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = https ? "https" : "http";
        if (cookieName is not null)
        {
            ctx.Request.Headers.Cookie = $"{cookieName}={value}";
        }

        return ctx;
    }

    [Fact]
    public async Task No_cookie_reads_nobody()
    {
        var (reader, _, _) = Make();
        Assert.Null(await reader.ReadAsync(Request(null, null, https: false), Ct));
    }

    [Fact]
    public async Task A_valid_cookie_reads_the_session_on_plain_http()
    {
        var (reader, store, time) = Make();
        var identity = new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());
        var created = await store.CreateAsync(identity, time.GetUtcNow(), Ct);

        var session = await reader.ReadAsync(Request("bazaar-admin", created.CookieValue, https: false), Ct);

        Assert.Equal(new AdminSession(identity.PrincipalId, PrincipalKind.Human, identity.DeviceId), session);
    }

    [Fact]
    public async Task The_host_prefixed_cookie_is_read_on_https_and_the_plain_one_is_ignored()
    {
        var (reader, store, time) = Make();
        var identity = new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());
        var created = await store.CreateAsync(identity, time.GetUtcNow(), Ct);

        Assert.NotNull(await reader.ReadAsync(Request("__Host-bazaar-admin", created.CookieValue, https: true), Ct));
        Assert.Null(await reader.ReadAsync(Request("bazaar-admin", created.CookieValue, https: true), Ct));
        Assert.Null(await reader.ReadAsync(Request("__Host-bazaar-admin", created.CookieValue, https: false), Ct));
    }

    [Fact]
    public async Task Unknown_expired_and_oversized_cookies_read_nobody()
    {
        var (reader, store, time) = Make();
        var created = await store.CreateAsync(new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid()), time.GetUtcNow(), Ct);

        Assert.Null(await reader.ReadAsync(Request("bazaar-admin", "nope", https: false), Ct));
        Assert.Null(await reader.ReadAsync(Request("bazaar-admin", new string('a', 600), https: false), Ct));

        time.Advance(AdminSessionOptions.MaxSliding + TimeSpan.FromSeconds(1));
        Assert.Null(await reader.ReadAsync(Request("bazaar-admin", created.CookieValue, https: false), Ct));
    }
}
