using System.Globalization;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Roles.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.WebAdmin.Http;

/// <summary>
/// Dispatches every request under <c>/admin</c> (doc 51 section 5): 404 for an unknown path, 405 with <c>Allow</c> for a wrong method,
/// 401 without a valid session, 403 for a failed gate or write protection, then the handler. Security headers go on every response.
/// </summary>
internal static class AdminRouter
{
    private delegate Task Handler(AdminRequest r);

    private sealed record Route(string Method, string Path, Handler Handle, bool Public = false, bool Write = false, bool JsonBody = false, string? Section = null);

    private static readonly Route[] _routes =
    [
        new("GET", "/admin", ShellAsync, Public: true),
        new("GET", "/admin/session/redeem", RedeemAsync, Public: true),
        new("POST", "/admin/api/signout", SignOutAsync, Write: true),
        new("GET", "/admin/api/session", SessionAsync, Section: "status"),
        new("GET", "/admin/api/status", r => ReadAsync<IAdminStatusSource, AdminStatusView>(r, "status", s => s.GetAsync(r.Ct), AdminJson.Default.AdminStatusView), Section: "status"),
        new("GET", "/admin/api/kodo", r => ReadAsync<IAdminKodoSource, AdminKodoView>(r, "kodo", s => s.GetAsync(r.Ct), AdminJson.Default.AdminKodoView), Section: "kodo"),
        new("GET", "/admin/api/users", r => ReadAsync<IAdminUserDirectory, IReadOnlyList<AdminUser>>(r, "users", s => s.ListAsync(r.Ct), AdminJson.Default.IReadOnlyListAdminUser), Section: "users"),
        new("GET", "/admin/api/devices", r => ReadAsync<IAdminDeviceDirectory, IReadOnlyList<AdminDevice>>(r, "devices", s => s.ListAsync(r.Ct), AdminJson.Default.IReadOnlyListAdminDevice), Section: "devices"),
        new("GET", "/admin/api/audit", AuditAsync, Section: "audit"),
        new("GET", "/admin/api/approvals", r => ReadAsync<IAdminApprovalSource, IReadOnlyList<AdminApproval>>(r, "approvals", s => s.ListPendingAsync(r.Ct), AdminJson.Default.IReadOnlyListAdminApproval), Section: "approvals"),
        new("GET", "/admin/api/modules", r => ReadAsync<IAdminModuleSelection, AdminModuleCatalogue>(r, "modules", s => s.GetCatalogueAsync(r.Ct), AdminJson.Default.AdminModuleCatalogue), Section: "modules"),
        new("GET", "/admin/api/modules/builds", r => ReadAsync<IAdminModuleSelection, IReadOnlyList<AdminBuildRecord>>(r, "modules", s => s.GetBuildsAsync(r.Ct), AdminJson.Default.IReadOnlyListAdminBuildRecord), Section: "modules"),
        new("POST", "/admin/api/modules/preview", PreviewAsync, Write: true, JsonBody: true, Section: "modules"),
        new("PUT", "/admin/api/modules/selection", CommitAsync, Write: true, JsonBody: true, Section: "modules"),
        new("POST", "/admin/api/modules/rebuild", RebuildAsync, Write: true, Section: "modules"),
        // Gateway catalogue (doc 05 manual "check for newer catalogue"): gated as the Modules area, no new capability (PROPOSED).
        new("GET", "/admin/api/gateway/catalogue", r => ReadAsync<IAdminCatalogueRefresh, AdminCatalogueReport>(r, "gateway-catalogue", s => s.GetLastAsync(r.Ct), AdminJson.Default.AdminCatalogueReport), Section: "modules"),
        new("POST", "/admin/api/gateway/catalogue/refresh", CatalogueRefreshAsync, Write: true, Section: "modules"),
    ];

    /// <summary>Maps each panel section (its route segment) to the <see cref="AdminArea"/> the gate checks for it.</summary>
    internal static readonly (string Section, AdminArea Area)[] SectionAreas =
    [
        ("status", AdminArea.Overview),
        ("kodo", AdminArea.Kodo),
        ("audit", AdminArea.Audit),
        ("users", AdminArea.Members),
        ("devices", AdminArea.Devices),
        ("approvals", AdminArea.Overview),
        ("modules", AdminArea.Modules),
    ];

    private static AdminArea AreaFor(string section) => SectionAreas.First(x => x.Section == section).Area;

    private const string _assetPrefix = "/admin/assets/";

    public static async Task HandleAsync(HttpContext ctx)
    {
        var path = NormalizePath(ctx.Request.Path.Value);
        AdminHttp.ApplySecurityHeaders(ctx, api: path.StartsWith("/admin/api/", StringComparison.OrdinalIgnoreCase));
        var method = ctx.Request.Method;

        Route? route = null;
        var allowed = new List<string>(2);
        var isAsset = path.StartsWith(_assetPrefix, StringComparison.OrdinalIgnoreCase) && path.Length > _assetPrefix.Length;
        if (isAsset)
        {
            allowed.Add("GET");
        }
        else
        {
            foreach (var candidate in _routes)
            {
                if (string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    allowed.Add(candidate.Method);
                    if (candidate.Method == method)
                    {
                        route = candidate;
                    }
                }
            }
        }

        if (allowed.Count == 0)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (isAsset ? method != "GET" : route is null)
        {
            ctx.Response.Headers.Allow = string.Join(", ", allowed);
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var r = new AdminRequest(ctx, ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow());
        if (isAsset)
        {
            await AssetAsync(r, path[_assetPrefix.Length..]);
            return;
        }

        if (route is { Public: false })
        {
            // Session-only routes (sign-out) need a signed-in principal but no area, so an account without admin rights can still end its session.
            if (route.Section is null)
            {
                r.Session = await r.Required<IAdminSessionReader>().ReadAsync(ctx, r.Ct);
                if (r.Session is null)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            else
            {
                // The gate reads the session, checks the area's capability and audits its own denials (admin.web.denied), so a 403 here is not audited twice.
                var access = await r.Required<IAdminGate>().CheckAsync(ctx, AreaFor(route.Section), r.Ct);
                if (access.Decision != AdminDecision.Allowed || access.Session is null)
                {
                    ctx.Response.StatusCode = access.Decision == AdminDecision.Unauthenticated || access.Session is null
                        ? StatusCodes.Status401Unauthorized
                        : StatusCodes.Status403Forbidden;
                    return;
                }

                r.Session = access.Session;
            }

            if (route.Write && !AdminHttp.PassesWriteProtection(ctx.Request, route.JsonBody))
            {
                await DenyAsync(r, "write-protection", route.Path, route.Section);
                return;
            }
        }

        await route!.Handle(r);
    }

    private static string NormalizePath(string? value)
    {
        var p = string.IsNullOrEmpty(value) ? "/" : value;
        return p.Length > 1 && p.EndsWith('/') ? p.TrimEnd('/') : p;
    }

    /// <summary>403 with no body, audited as <c>admin.web.denied</c> on a best-effort basis (the request is refused either way).</summary>
    private static string Actor(AdminSession? session) => session is null ? "anonymous" : session.PrincipalId.ToString("D");

    private static async Task DenyAsync(AdminRequest r, string reason, string path, string? section = null)
    {
        var fields = section is null
            ? new[] { ("reason", reason), ("path", path) }
            : [("reason", reason), ("path", path), ("section", section)];
        await AdminHttp.TryAuditAsync(r, AdminAuditKinds.Denied, Actor(r.Session), fields);
        r.Http.Response.StatusCode = StatusCodes.Status403Forbidden;
    }

    private static async Task ServeAssetAsync(AdminRequest r, string relative)
    {
        var provider = r.Required<IAdminAssetProvider>();
        if (!provider.TryGet(relative, out var content, out var contentType))
        {
            r.Http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        r.Http.Response.ContentType = contentType;
        r.Http.Response.Headers.CacheControl = "no-cache";
        await r.Http.Response.Body.WriteAsync(content, r.Ct);
    }

    private static Task ShellAsync(AdminRequest r) => ServeAssetAsync(r, "index.html");

    private static Task AssetAsync(AdminRequest r, string rest)
    {
        if (rest.Length == 0 || rest.Length > 200 || rest.StartsWith('/') || rest.Contains("..", StringComparison.Ordinal)
            || rest.Contains('\\') || rest.Contains('\0') || rest.Contains("//", StringComparison.Ordinal) || rest.Contains(':'))
        {
            r.Http.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return ServeAssetAsync(r, "assets/" + rest);
    }

    private static async Task RedeemAsync(AdminRequest r)
    {
        var ctx = r.Http;
        var code = ctx.Request.Query["code"].ToString();
        AdminIdentity? identity = null;
        if (code.Length is > 0 and <= 256 && r.Service<IAdminHandoffRedeemer>() is { } redeemer)
        {
            identity = await redeemer.RedeemAsync(code, r.Ct);
        }

        if (identity is null)
        {
            await AdminHttp.TryAuditAsync(r, AdminAuditKinds.Denied, "anonymous", ("reason", "bad-handoff-code"), ("path", "/admin/session/redeem"));
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Agents never sign in to the panel (doc 48); the per-request gate would refuse them anyway, so no cookie is issued.
        if (identity.Kind != PrincipalKind.Human)
        {
            await AdminHttp.TryAuditAsync(r, AdminAuditKinds.Denied, identity.PrincipalId.ToString("D"), ("reason", "not-human"), ("path", "/admin/session/redeem"));
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var created = await r.Required<IAdminSessionStore>().CreateAsync(identity, r.Now, r.Ct);
        if (!await AdminHttp.TryAuditAsync(r, AdminAuditKinds.SignIn, identity.PrincipalId.ToString("D"), ("deviceId", identity.DeviceId.ToString("D"))))
        {
            await r.Required<IAdminSessionStore>().RevokeAsync(created.Session.Id, r.Ct);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        AdminHttp.SetCookie(ctx, created.CookieValue);
        ctx.Response.Redirect("/admin");
    }

    private static async Task SignOutAsync(AdminRequest r)
    {
        // Ending a session is never blocked by the audit sink: the entry is best effort, the revocation is not.
        await AdminHttp.TryAuditAsync(r, AdminAuditKinds.SignOut, Actor(r.Session));
        if (AdminHttp.ReadCookie(r.Http.Request) is { } cookie
            && await r.Required<IAdminSessionStore>().ValidateAsync(cookie, r.Now, r.Ct) is { } panel)
        {
            await r.Required<IAdminSessionStore>().RevokeAsync(panel.Id, r.Ct);
        }

        AdminHttp.ClearCookie(r.Http);
        r.Http.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Principal, kind and the areas and sections the gate allows. The gate is asked once per distinct area.</summary>
    private static async Task SessionAsync(AdminRequest r)
    {
        var s = r.Session!;
        var gate = r.Required<IAdminGate>();
        // Reaching this handler means the Overview check already passed, so it is allowed without a second gate call.
        var allowedAreas = new List<AdminArea> { AdminArea.Overview };
        foreach (var area in SectionAreas.Select(x => x.Area).Distinct().Where(a => a != AdminArea.Overview))
        {
            if ((await gate.CheckAsync(r.Http, area, r.Ct)).Decision == AdminDecision.Allowed)
            {
                allowedAreas.Add(area);
            }
        }

        var view = new AdminSessionView(
            s.PrincipalId,
            s.Kind.ToString(),
            allowedAreas.Select(a => a.ToString()).ToArray(),
            SectionAreas.Where(x => allowedAreas.Contains(x.Area)).Select(x => x.Section).ToArray());
        await AdminHttp.JsonAsync(r.Http, StatusCodes.Status200OK, view, AdminJson.Default.AdminSessionView);
    }

    private static async Task ReadAsync<TPort, T>(AdminRequest r, string section, Func<TPort, Task<T>> load, JsonTypeInfo<T> info)
        where TPort : class
    {
        if (!await AdminHttp.TryAuditAsync(r, AdminAuditKinds.Read, Actor(r.Session), ("section", section)))
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (r.Service<TPort>() is not { } port)
        {
            await AdminHttp.UnavailableAsync(r.Http);
            return;
        }

        await AdminHttp.DataAsync(r.Http, await load(port), info);
    }

    private static Task AuditAsync(AdminRequest r)
    {
        var q = r.Http.Request.Query;
        long? before = null;
        var limit = 50;
        if (q.ContainsKey("before"))
        {
            if (!long.TryParse(q["before"], NumberStyles.None, CultureInfo.InvariantCulture, out var b))
            {
                return AdminHttp.EmptyAsync(r.Http, StatusCodes.Status400BadRequest);
            }

            before = b;
        }

        if (q.ContainsKey("limit"))
        {
            if (!int.TryParse(q["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out var l))
            {
                return AdminHttp.EmptyAsync(r.Http, StatusCodes.Status400BadRequest);
            }

            limit = Math.Clamp(l, 1, 200);
        }

        return ReadAsync<IAdminAuditReader, AdminAuditPage>(r, "audit", s => s.ReadAsync(before, limit, r.Ct), AdminJson.Default.AdminAuditPage);
    }

    private static async Task PreviewAsync(AdminRequest r)
    {
        var body = await AdminHttp.ReadBodyAsync(r.Http, AdminJson.Default.AdminPreviewRequest);
        if (body?.Selected is null)
        {
            r.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (r.Service<IAdminModuleSelection>() is not { } port)
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var preview = await port.PreviewAsync(body.Selected, r.Ct);
        await AdminHttp.JsonAsync(r.Http, StatusCodes.Status200OK, preview, AdminJson.Default.AdminSelectionPreview);
    }

    private static async Task CommitAsync(AdminRequest r)
    {
        var body = await AdminHttp.ReadBodyAsync(r.Http, AdminJson.Default.AdminSelectionRequest);
        if (body?.Selected is null || body.ExpectedRevision is null)
        {
            r.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (r.Service<IAdminModuleSelection>() is not { } port)
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var actor = r.Session!.PrincipalId;
        if (!await AdminHttp.TryAuditAsync(
                r,
                AdminAuditKinds.SelectionCommit,
                Actor(r.Session),
                ("expectedRevision", body.ExpectedRevision.Value.ToString(CultureInfo.InvariantCulture)),
                ("selected", string.Join(",", body.Selected))))
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var result = await port.CommitAsync(actor, body.Selected, body.ExpectedRevision.Value, r.Ct);
        await AdminHttp.JsonAsync(r.Http, result.Committed ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, result, AdminJson.Default.AdminSelectionCommit);
    }

    private static async Task RebuildAsync(AdminRequest r)
    {
        var key = r.Http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128)
        {
            r.Http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (r.Service<IAdminModuleSelection>() is not { } port)
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var actor = r.Session!.PrincipalId;
        if (!await AdminHttp.TryAuditAsync(r, AdminAuditKinds.RebuildStart, Actor(r.Session), ("idempotencyKey", key)))
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var result = await port.StartRebuildAsync(actor, key, r.Ct);
        await AdminHttp.JsonAsync(r.Http, result.Started ? StatusCodes.Status200OK : StatusCodes.Status409Conflict, result, AdminJson.Default.AdminRebuildStart);
    }

    private static async Task CatalogueRefreshAsync(AdminRequest r)
    {
        if (r.Service<IAdminCatalogueRefresh>() is not { } port)
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var actor = r.Session!.PrincipalId;
        if (!await AdminHttp.TryAuditAsync(r, AdminAuditKinds.CatalogueRefresh, Actor(r.Session)))
        {
            r.Http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var report = await port.RefreshAsync(actor, r.Ct);
        await AdminHttp.JsonAsync(r.Http, StatusCodes.Status200OK, report, AdminJson.Default.AdminCatalogueReport);
    }
}
