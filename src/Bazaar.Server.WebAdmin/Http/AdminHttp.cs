using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Bazaar.Server.WebAdmin.Http;

/// <summary>Per-request state handed to a route handler.</summary>
internal sealed class AdminRequest(HttpContext http, DateTimeOffset now)
{
    public HttpContext Http { get; } = http;

    public DateTimeOffset Now { get; } = now;

    public AdminSession? Session { get; set; }

    public CancellationToken Ct => Http.RequestAborted;

    public T? Service<T>() where T : class => Http.RequestServices.GetService<T>();

    public T Required<T>() where T : notnull => Http.RequestServices.GetRequiredService<T>();
}

/// <summary>Response, cookie, origin and audit helpers shared by the panel's handlers (doc 51 section 5).</summary>
internal static class AdminHttp
{
    public const string SecureCookie = "__Host-bazaar-admin";
    public const string LoopbackCookie = "bazaar-admin";
    public const string CsrfHeader = "X-Bazaar-Admin";
    public const int MaxBodyBytes = 64 * 1024;

    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public static void ApplySecurityHeaders(HttpContext ctx, bool api)
    {
        var h = ctx.Response.Headers;
        h["Content-Security-Policy"] = ContentSecurityPolicy;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        if (api)
        {
            h["Cache-Control"] = "no-store";
        }
    }

    /// <summary>The cookie name for this request: the <c>__Host-</c> form over TLS, the plain form on loopback HTTP.</summary>
    public static string CookieName(HttpRequest request) => request.IsHttps ? SecureCookie : LoopbackCookie;

    /// <summary>The panel cookie value, or null when it is missing or implausibly long.</summary>
    public static string? ReadCookie(HttpRequest request)
    {
        var cookie = request.Cookies[CookieName(request)];
        return string.IsNullOrEmpty(cookie) || cookie.Length > 512 ? null : cookie;
    }

    private static CookieOptions CookieOptionsFor(HttpRequest request) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict,
        Path = "/admin",
        IsEssential = true,
    };

    public static void SetCookie(HttpContext ctx, string value) =>
        ctx.Response.Cookies.Append(CookieName(ctx.Request), value, CookieOptionsFor(ctx.Request));

    public static void ClearCookie(HttpContext ctx) =>
        ctx.Response.Cookies.Delete(CookieName(ctx.Request), CookieOptionsFor(ctx.Request));

    /// <summary>The panel origin (<c>scheme://host[:port]</c>) the request was addressed to.</summary>
    public static string RequestOrigin(HttpRequest request) => $"{request.Scheme}://{request.Host.Value}";

    /// <summary>Writes need <c>X-Bazaar-Admin: 1</c> and an <c>Origin</c> equal to the panel origin; a JSON body also needs a JSON content type.</summary>
    public static bool PassesWriteProtection(HttpRequest request, bool jsonBody)
    {
        if (request.Headers[CsrfHeader].ToString() != "1")
        {
            return false;
        }

        var origin = request.Headers.Origin;
        if (origin.Count != 1 || !string.Equals(origin[0], RequestOrigin(request), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (jsonBody)
        {
            return MediaTypeHeaderValue.TryParse(request.ContentType, out var mt)
                && string.Equals(mt.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    public static Task EmptyAsync(HttpContext ctx, int status)
    {
        ctx.Response.StatusCode = status;
        return Task.CompletedTask;
    }

    public static async Task JsonAsync<T>(HttpContext ctx, int status, T value, JsonTypeInfo<T> info)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, info), ctx.RequestAborted);
    }

    /// <summary>Writes <c>{"available":true,"data":...}</c>.</summary>
    public static async Task DataAsync<T>(HttpContext ctx, T value, JsonTypeInfo<T> info)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var body = new MemoryStream();
        await body.WriteAsync("{\"available\":true,\"data\":"u8.ToArray(), ctx.RequestAborted);
        await JsonSerializer.SerializeAsync(body, value, info, ctx.RequestAborted);
        await body.WriteAsync("}"u8.ToArray(), ctx.RequestAborted);
        body.Position = 0;
        await body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    public static Task UnavailableAsync(HttpContext ctx) => JsonAsync(ctx, StatusCodes.Status200OK, new AdminUnavailable(false), AdminJson.Default.AdminUnavailable);

    /// <summary>Reads a JSON body of at most <see cref="MaxBodyBytes"/>; null when it is missing, too large or malformed.</summary>
    public static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, JsonTypeInfo<T> info) where T : class
    {
        if (ctx.Request.ContentLength is > MaxBodyBytes)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + n > MaxBodyBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, n);
            }

            ms.Position = 0;
            return await JsonSerializer.DeserializeAsync(ms, info, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Appends an audit entry. Returns false when the entry cannot be appended (including when no audit sink is registered), so the
    /// caller can fail closed.
    /// </summary>
    public static async Task<bool> TryAuditAsync(AdminRequest r, string kind, string actor, params (string Key, string Value)[] fields)
    {
        try
        {
            var dict = new Dictionary<string, string>(fields.Length, StringComparer.Ordinal);
            foreach (var (k, v) in fields)
            {
                dict[k] = v;
            }

            await r.Required<IAdminAudit>().RecordAsync(kind, actor, dict, r.Ct);
            return true;
        }
        catch (OperationCanceledException) when (r.Ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
