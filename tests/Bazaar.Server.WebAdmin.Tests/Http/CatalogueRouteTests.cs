using System.Net;
using System.Text.Json;
using Bazaar.Server.WebAdmin.Tests.Http.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WebAdmin.Tests.Http;

public sealed class CatalogueRouteTests
{
    private const string _get = "/admin/api/gateway/catalogue";
    private const string _refresh = "/admin/api/gateway/catalogue/refresh";

    private sealed class FakeCatalogue : IAdminCatalogueRefresh
    {
        public int RefreshCalls { get; private set; }

        public Guid LastActor { get; private set; }

        private AdminCatalogueReport _last = new(null, []);

        public Task<AdminCatalogueReport> RefreshAsync(Guid actorPrincipalId, CancellationToken ct = default)
        {
            RefreshCalls++;
            LastActor = actorPrincipalId;
            _last = new AdminCatalogueReport(
                DateTimeOffset.UnixEpoch,
                [new AdminCatalogueProviderResult("p1", "refreshed", 2, 1, 0, ["x-new"])]);
            return Task.FromResult(_last);
        }

        public Task<AdminCatalogueReport> GetLastAsync(CancellationToken ct = default) => Task.FromResult(_last);
    }

    private static Task<AdminTestHost> Host() =>
        AdminTestHost.StartAsync(configure: s =>
        {
            s.AddSingleton<FakeCatalogue>();
            s.AddSingleton<IAdminCatalogueRefresh>(sp => sp.GetRequiredService<FakeCatalogue>());
        });

    [Theory]
    [InlineData("GET", _get)]
    [InlineData("POST", _refresh)]
    public async Task Routes_without_a_session_are_401_and_call_nothing(string method, string path)
    {
        await using var h = await Host();
        var r = await h.WriteAsync(method, path, "no-such-cookie");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal(0, h.Services.GetRequiredService<FakeCatalogue>().RefreshCalls);
    }

    [Theory]
    [InlineData("GET", _get)]
    [InlineData("POST", _refresh)]
    public async Task Routes_without_the_modules_capability_are_403(string method, string path)
    {
        await using var h = await Host();
        var r = await h.WriteAsync(method, path, h.Seed("kodo.view.status"));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(0, h.Services.GetRequiredService<FakeCatalogue>().RefreshCalls);
    }

    [Fact]
    public async Task Refresh_without_write_protection_is_403_and_does_nothing()
    {
        await using var h = await Host();
        var cookie = h.Seed();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync("POST", _refresh, cookie, csrf: false, origin: true, body: "{}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync("POST", _refresh, cookie, csrf: true, origin: false, body: "{}")).StatusCode);
        Assert.Equal(0, h.Services.GetRequiredService<FakeCatalogue>().RefreshCalls);
    }

    [Fact]
    public async Task Wrong_method_is_405_with_allow()
    {
        await using var h = await Host();
        var r = await h.SendAsync("GET", _refresh, h.Seed());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        Assert.Contains("POST", r.Content.Headers.Allow);
    }

    [Fact]
    public async Task Refresh_returns_the_per_provider_result_audits_and_records_the_actor()
    {
        await using var h = await Host();
        var r = await h.WriteAsync("POST", _refresh, h.Seed());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = JsonElement.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var p = j.GetProperty("providers")[0];
        Assert.Equal("p1", p.GetProperty("providerId").GetString());
        Assert.Equal("refreshed", p.GetProperty("outcome").GetString());
        Assert.Equal(2, p.GetProperty("added").GetInt32());
        Assert.Equal(1, p.GetProperty("missing").GetInt32());
        Assert.Equal(0, p.GetProperty("returned").GetInt32());
        Assert.Equal("x-new", p.GetProperty("unclassifiedIds")[0].GetString());
        Assert.Equal(h.LastPrincipal, h.Services.GetRequiredService<FakeCatalogue>().LastActor);
        var entry = Assert.Single(h.Audit.Of(AdminAuditKinds.CatalogueRefresh));
        Assert.Equal(h.LastPrincipal.ToString("D"), entry.Actor);
    }

    [Fact]
    public async Task Refresh_is_refused_when_the_audit_entry_cannot_be_written()
    {
        await using var h = await Host();
        var cookie = h.Seed();
        h.Audit.Throw = true;
        var r = await h.WriteAsync("POST", _refresh, cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(0, h.Services.GetRequiredService<FakeCatalogue>().RefreshCalls);
    }

    [Fact]
    public async Task Get_returns_the_last_result_and_is_audited_as_a_read()
    {
        await using var h = await Host();
        var cookie = h.Seed();
        var empty = JsonElement.Parse(await (await h.SendAsync("GET", _get, cookie)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("data").GetProperty("ranAt").ValueKind);

        await h.WriteAsync("POST", _refresh, cookie);
        var r = await h.SendAsync("GET", _get, cookie);
        var j = JsonElement.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("p1", j.GetProperty("data").GetProperty("providers")[0].GetProperty("providerId").GetString());
        Assert.Contains(h.Audit.Of(AdminAuditKinds.Read), e => e.Fields["section"] == "gateway-catalogue");
    }

    [Fact]
    public async Task Missing_port_is_unavailable_on_get_and_503_on_refresh()
    {
        await using var h = await AdminTestHost.StartAsync(withPorts: false);
        var cookie = h.Seed();
        var get = JsonElement.Parse(await (await h.SendAsync("GET", _get, cookie)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(get.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await h.WriteAsync("POST", _refresh, cookie)).StatusCode);
    }
}
