using System.Net;
using System.Text.Json;
using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin.Http;
using Bazaar.Server.WebAdmin.Tests.Http.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WebAdmin.Tests.Http;

public sealed class AdminHttpTests
{
    private const string _csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    private static readonly string[] _readRoutes =
    [
        "/admin/api/status", "/admin/api/kodo", "/admin/api/users", "/admin/api/devices", "/admin/api/audit", "/admin/api/approvals",
        "/admin/api/modules", "/admin/api/modules/builds",
    ];

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => JsonElement.Parse(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static void AssertHeaders(HttpResponseMessage r, bool api)
    {
        Assert.Equal(_csp, Assert.Single(r.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(r.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(r.Headers.GetValues("Referrer-Policy")));
        if (api)
        {
            Assert.Equal("no-store", Assert.Single(r.Headers.GetValues("Cache-Control")));
        }
    }

    [Fact]
    public async Task Shell_is_public_and_has_security_headers()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", "/admin");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("<html></html>", await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("text/html", r.Content.Headers.ContentType?.MediaType);
        AssertHeaders(r, api: false);
        Assert.Equal(HttpStatusCode.OK, (await h.SendAsync("GET", "/admin/")).StatusCode);
    }

    [Fact]
    public async Task Assets_are_public_and_unknown_is_404()
    {
        await using var h = await AdminTestHost.StartAsync();
        var ok = await h.SendAsync("GET", "/admin/assets/panel.js");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        AssertHeaders(ok, api: false);
        var missing = await h.SendAsync("GET", "/admin/assets/nope.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        AssertHeaders(missing, api: false);
        Assert.Equal(HttpStatusCode.NotFound, (await h.SendAsync("GET", "/admin/other")).StatusCode);
    }

    [Theory]
    [InlineData("/admin/assets/../index.html")]
    [InlineData("/admin/assets/%2e%2e/index.html")]
    [InlineData("/admin/assets/..%2findex.html")]
    [InlineData("/admin/assets/a%5cb.js")]
    [InlineData("/admin/assets//etc/passwd")]
    [InlineData("/admin/assets/x:y")]
    public async Task Asset_path_traversal_never_reaches_the_provider(string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", path);
        Assert.NotEqual(HttpStatusCode.OK, r.StatusCode);
        var assets = (FakeAssets)h.Services.GetRequiredService<IAdminAssetProvider>();
        Assert.DoesNotContain(assets.Asked, p => p.Contains("..", StringComparison.Ordinal) || p.Contains('\\'));
    }

    [Theory]
    [InlineData("POST", "/admin", "GET")]
    [InlineData("DELETE", "/admin/assets/panel.js", "GET")]
    [InlineData("HEAD", "/admin", "GET")]
    [InlineData("PUT", "/admin/api/status", "GET")]
    [InlineData("DELETE", "/admin/api/modules/selection", "PUT")]
    [InlineData("GET", "/admin/api/modules/rebuild", "POST")]
    [InlineData("PATCH", "/admin/api/signout", "POST")]
    [InlineData("POST", "/admin/session/redeem", "GET")]
    public async Task Wrong_method_is_405_with_Allow(string method, string path, string allow)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync(method, path, h.Seed());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
        Assert.Equal(allow, Assert.Single(r.Content.Headers.Allow));
        AssertHeaders(r, path.StartsWith("/admin/api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redeem_sets_loopback_cookie_audits_and_redirects()
    {
        await using var h = await AdminTestHost.StartAsync();
        h.Redeemer.Codes["good"] = new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());
        var r = await h.SendAsync("GET", "/admin/session/redeem?code=good");
        Assert.Equal(HttpStatusCode.Found, r.StatusCode);
        Assert.Equal("/admin", r.Headers.Location?.OriginalString);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("bazaar-admin=created-", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/admin", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("__Host-", cookie, StringComparison.Ordinal);
        Assert.Single(h.Audit.Of(AdminAuditKinds.SignIn));
        AssertHeaders(r, api: false);

        // The code is single use.
        var again = await h.SendAsync("GET", "/admin/session/redeem?code=good");
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Theory]
    [InlineData("/admin/session/redeem?code=bad")]
    [InlineData("/admin/session/redeem")]
    public async Task Redeem_with_bad_code_is_401_audited_denied(string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", path);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Single(h.Audit.Of(AdminAuditKinds.Denied));
    }

    [Fact]
    public async Task Redeem_fails_closed_when_audit_is_down()
    {
        await using var h = await AdminTestHost.StartAsync();
        h.Redeemer.Codes["good"] = new AdminIdentity(Guid.NewGuid(), PrincipalKind.Human, Guid.NewGuid());
        h.Audit.Throw = true;
        var r = await h.SendAsync("GET", "/admin/session/redeem?code=good");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Single(h.Sessions.Revoked);
    }

    [Fact]
    public async Task Redeem_for_a_non_human_principal_is_403_and_issues_no_session()
    {
        await using var h = await AdminTestHost.StartAsync();
        h.Redeemer.Codes["agent"] = new AdminIdentity(Guid.NewGuid(), PrincipalKind.Agent, Guid.NewGuid());
        var r = await h.SendAsync("GET", "/admin/session/redeem?code=agent");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Single(h.Audit.Of(AdminAuditKinds.Denied));
        Assert.Empty(h.Audit.Of(AdminAuditKinds.SignIn));
    }

    [Theory]
    [MemberData(nameof(AllApiRoutes))]
    public async Task Api_without_session_is_401_no_body(string method, string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync(method, path, csrf: true, origin: true, body: "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        AssertHeaders(r, api: true);

        var bad = await h.SendAsync(method, path, "not-a-session", csrf: true, origin: true, body: "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
    }

    public static TheoryData<string, string> AllApiRoutes()
    {
        var d = new TheoryData<string, string>();
        foreach (var p in _readRoutes)
        {
            d.Add("GET", p);
        }

        d.Add("GET", "/admin/api/session");
        d.Add("POST", "/admin/api/signout");
        d.Add("POST", "/admin/api/modules/preview");
        d.Add("PUT", "/admin/api/modules/selection");
        d.Add("POST", "/admin/api/modules/rebuild");
        return d;
    }

    [Fact]
    public async Task Session_without_panel_access_is_403_no_body_and_audited()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed("chat.send");
        var r = await h.SendAsync("GET", "/admin/api/session", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(h.Audit.Of(AdminAuditKinds.Denied));
    }

    [Fact]
    public async Task Session_endpoint_lists_principal_kind_areas_and_sections()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed("admin.modules", "kodo.view.status");
        var r = await h.SendAsync("GET", "/admin/api/session", cookie);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        AssertHeaders(r, api: true);
        var j = await JsonOf(r);
        Assert.Equal(h.LastPrincipal, j.GetProperty("principalId").GetGuid());
        Assert.Equal("Human", j.GetProperty("kind").GetString());
        Assert.False(j.TryGetProperty("capabilities", out _));
        Assert.Equal(["Overview", "Kodo", "Modules"], j.GetProperty("areas").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(["status", "kodo", "approvals", "modules"], j.GetProperty("sections").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("/admin/api/kodo", "admin.modules")]
    [InlineData("/admin/api/users", "kodo.view.status")]
    [InlineData("/admin/api/devices", "admin.members")]
    [InlineData("/admin/api/audit", "admin.devices")]
    [InlineData("/admin/api/approvals", "chat.send")]
    [InlineData("/admin/api/modules", "kodo.view.status")]
    [InlineData("/admin/api/modules/builds", "admin.members")]
    public async Task Section_without_its_capability_is_403_and_audited_and_port_not_called(string path, string heldCapability)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", path, h.Seed(heldCapability));
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Single(h.Audit.Of(AdminAuditKinds.Denied));
        Assert.Empty(h.Audit.Of(AdminAuditKinds.Read));
    }

    [Theory]
    [MemberData(nameof(ReadRouteData))]
    public async Task Read_sections_return_data_and_audit_read_with_section(string path, string section)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", path, h.Seed());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        AssertHeaders(r, api: true);
        var j = await JsonOf(r);
        Assert.True(j.GetProperty("available").GetBoolean());
        Assert.True(j.TryGetProperty("data", out _));
        var read = Assert.Single(h.Audit.Of(AdminAuditKinds.Read));
        Assert.Equal(h.LastPrincipal.ToString("D"), read.Actor);
        Assert.Equal(section, read.Fields["section"]);
    }

    public static TheoryData<string, string> ReadRouteData() => new()
    {
        { "/admin/api/status", "status" },
        { "/admin/api/kodo", "kodo" },
        { "/admin/api/users", "users" },
        { "/admin/api/devices", "devices" },
        { "/admin/api/audit", "audit" },
        { "/admin/api/approvals", "approvals" },
        { "/admin/api/modules", "modules" },
        { "/admin/api/modules/builds", "modules" },
    };

    [Theory]
    [MemberData(nameof(ReadRouteData))]
    public async Task Missing_port_is_a_soft_edge_200_unavailable(string path, string section)
    {
        await using var h = await AdminTestHost.StartAsync(withPorts: false);
        var r = await h.SendAsync("GET", path, h.Seed());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await JsonOf(r);
        Assert.False(j.GetProperty("available").GetBoolean());
        Assert.False(j.TryGetProperty("data", out _));
        Assert.Equal(section, Assert.Single(h.Audit.Of(AdminAuditKinds.Read)).Fields["section"]);
    }

    [Fact]
    public async Task Read_fails_closed_when_audit_is_down()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        h.Audit.Throw = true;
        var r = await h.SendAsync("GET", "/admin/api/status", cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Empty(await r.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("", null, 50)]
    [InlineData("?limit=0", null, 1)]
    [InlineData("?limit=9999", null, 200)]
    [InlineData("?limit=25&before=40", 40L, 25)]
    public async Task Audit_clamps_limit_and_passes_before(string query, long? before, int limit)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", "/admin/api/audit" + query, h.Seed());
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var reader = h.Services.GetRequiredService<FakeAuditReader>();
        Assert.Equal(before, reader.LastBefore);
        Assert.Equal(limit, reader.LastLimit);
    }

    [Theory]
    [InlineData("?limit=abc")]
    [InlineData("?before=-1")]
    public async Task Audit_rejects_malformed_paging(string query)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync("GET", "/admin/api/audit" + query, h.Seed());
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Signout_revokes_clears_cookie_and_audits()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        var r = await h.WriteAsync("POST", "/admin/api/signout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var set = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("bazaar-admin=;", set, StringComparison.Ordinal);
        Assert.Contains("path=/admin", set, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Audit.Of(AdminAuditKinds.SignOut));
        Assert.Single(h.Sessions.Revoked);
        Assert.Equal(HttpStatusCode.Unauthorized, (await h.SendAsync("GET", "/admin/api/session", cookie)).StatusCode);
    }

    [Fact]
    public async Task Signout_still_revokes_when_audit_is_down()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        h.Audit.Throw = true;
        var r = await h.WriteAsync("POST", "/admin/api/signout", cookie);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        Assert.Single(h.Sessions.Revoked);
    }

    public static TheoryData<string, string> WriteRoutes() => new()
    {
        { "POST", "/admin/api/signout" },
        { "POST", "/admin/api/modules/preview" },
        { "PUT", "/admin/api/modules/selection" },
        { "POST", "/admin/api/modules/rebuild" },
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Write_without_csrf_header_is_403(string method, string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync(method, path, h.Seed(), csrf: false, origin: true, body: "{}", idempotencyKey: "k");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(0, h.Modules!.CommitCalls + h.Modules.RebuildCalls);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task Write_without_or_with_wrong_origin_is_403(string method, string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(method, path, cookie, csrf: true, origin: false, body: "{}", idempotencyKey: "k")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(method, path, cookie, csrf: true, body: "{}", originOverride: "http://evil.example", idempotencyKey: "k")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.SendAsync(method, path, cookie, csrf: true, body: "{}", originOverride: "https://" + h.BaseUri.Authority, idempotencyKey: "k")).StatusCode);
    }

    [Theory]
    [InlineData("POST", "/admin/api/modules/preview")]
    [InlineData("PUT", "/admin/api/modules/selection")]
    public async Task Write_with_a_non_json_content_type_is_403(string method, string path)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.SendAsync(method, path, h.Seed(), csrf: true, origin: true, body: "{}", contentType: "text/plain");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        var none = await h.SendAsync(method, path, h.Seed(), csrf: true, origin: true, body: "{}", contentType: null);
        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
    }

    [Fact]
    public async Task Write_with_missing_capability_is_403_and_audited()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync("POST", "/admin/api/modules/rebuild", h.Seed("kodo.view.status"), idempotencyKey: "k");
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Single(h.Audit.Of(AdminAuditKinds.Denied));
        Assert.Equal(0, h.Modules!.RebuildCalls);
    }

    [Fact]
    public async Task Preview_returns_the_dry_run_and_writes_nothing()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync("POST", "/admin/api/modules/preview", h.Seed(), "{\"selected\":[\"m1\",\"m2\"]}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        AssertHeaders(r, api: true);
        var j = await JsonOf(r);
        Assert.True(j.GetProperty("valid").GetBoolean());
        Assert.Equal(["m1", "m2"], j.GetProperty("added").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Empty(h.Audit.Of(AdminAuditKinds.SelectionCommit));
    }

    [Theory]
    [InlineData("POST", "/admin/api/modules/preview", "{}")]
    [InlineData("POST", "/admin/api/modules/preview", "not json")]
    [InlineData("PUT", "/admin/api/modules/selection", "{\"selected\":[\"a\"]}")]
    [InlineData("PUT", "/admin/api/modules/selection", "{\"expectedRevision\":1}")]
    public async Task Malformed_body_is_400(string method, string path, string body)
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync(method, path, h.Seed(), body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(0, h.Modules!.CommitCalls);
    }

    [Fact]
    public async Task Commit_succeeds_with_current_revision_and_audits_first()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync("PUT", "/admin/api/modules/selection", h.Seed(), "{\"selected\":[\"m1\"],\"expectedRevision\":3}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await JsonOf(r);
        Assert.True(j.GetProperty("committed").GetBoolean());
        Assert.Equal(4, j.GetProperty("revision").GetInt64());
        var e = Assert.Single(h.Audit.Of(AdminAuditKinds.SelectionCommit));
        Assert.Equal(h.LastPrincipal.ToString("D"), e.Actor);
        Assert.Equal("3", e.Fields["expectedRevision"]);
    }

    [Fact]
    public async Task Commit_with_stale_revision_is_409_with_current_revision()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync("PUT", "/admin/api/modules/selection", h.Seed(), "{\"selected\":[\"m1\"],\"expectedRevision\":1}");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        var j = await JsonOf(r);
        Assert.False(j.GetProperty("committed").GetBoolean());
        Assert.Equal(3, j.GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Commit_fails_closed_when_audit_is_down_and_port_is_not_called()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        h.Audit.Throw = true;
        var r = await h.WriteAsync("PUT", "/admin/api/modules/selection", cookie, "{\"selected\":[\"m1\"],\"expectedRevision\":3}");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(0, h.Modules!.CommitCalls);
    }

    [Fact]
    public async Task Rebuild_requires_an_idempotency_key()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        Assert.Equal(HttpStatusCode.BadRequest, (await h.WriteAsync("POST", "/admin/api/modules/rebuild", cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.WriteAsync("POST", "/admin/api/modules/rebuild", cookie, idempotencyKey: " ")).StatusCode);
        Assert.Equal(0, h.Modules!.RebuildCalls);
        Assert.Empty(h.Audit.Of(AdminAuditKinds.RebuildStart));
    }

    [Fact]
    public async Task Rebuild_starts_and_audits()
    {
        await using var h = await AdminTestHost.StartAsync();
        var r = await h.WriteAsync("POST", "/admin/api/modules/rebuild", h.Seed(), idempotencyKey: "key-1");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("b2", (await JsonOf(r)).GetProperty("buildId").GetString());
        Assert.Equal("key-1", h.Modules!.LastIdempotencyKey);
        Assert.Single(h.Audit.Of(AdminAuditKinds.RebuildStart));
    }

    [Fact]
    public async Task Rebuild_while_one_runs_is_409()
    {
        await using var h = await AdminTestHost.StartAsync();
        h.Modules!.RebuildStarts = false;
        var r = await h.WriteAsync("POST", "/admin/api/modules/rebuild", h.Seed(), idempotencyKey: "k");
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False((await JsonOf(r)).GetProperty("started").GetBoolean());
    }

    [Fact]
    public async Task Rebuild_fails_closed_when_audit_is_down()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        h.Audit.Throw = true;
        var r = await h.WriteAsync("POST", "/admin/api/modules/rebuild", cookie, idempotencyKey: "k");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Equal(0, h.Modules!.RebuildCalls);
    }

    [Fact]
    public async Task Write_without_module_port_is_503_and_not_audited()
    {
        await using var h = await AdminTestHost.StartAsync(withPorts: false);
        var r = await h.WriteAsync("POST", "/admin/api/modules/rebuild", h.Seed(), idempotencyKey: "k");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode);
        Assert.Empty(h.Audit.Of(AdminAuditKinds.RebuildStart));
    }

    [Fact]
    public async Task Every_response_class_carries_security_headers()
    {
        await using var h = await AdminTestHost.StartAsync();
        var cookie = h.Seed();
        foreach (var (m, p) in new[] { ("GET", "/admin"), ("GET", "/admin/nope"), ("POST", "/admin"), ("GET", "/admin/api/status"), ("GET", "/admin/api/kodo") })
        {
            AssertHeaders(await h.SendAsync(m, p, cookie), p.StartsWith("/admin/api/", StringComparison.Ordinal));
        }

        AssertHeaders(await h.SendAsync("GET", "/admin/api/users", h.Seed("kodo.view.status")), api: true);
    }
}
