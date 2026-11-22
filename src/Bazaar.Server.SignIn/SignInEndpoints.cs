using System.Net;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.SignIn.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Bazaar.Server.SignIn;

/// <summary>
/// Maps the sign-in page under <c>/signin</c> (doc 32 section 4). Nothing is served unless <c>signin:enabled</c> or
/// <c>signin:adminEnabled</c> is on. Every failure answers the same generic body, so a caller cannot tell an unknown credential from a
/// bad one. <c>/signin/register</c> and <c>/signin/recover</c> are separate slices and answer 501 with a generic body.
/// </summary>
public static class SignInEndpoints
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    private const int _maxBodyBytes = 32 * 1024;
    private const string _genericError = "Sign-in failed.";

    public static IEndpointRouteBuilder MapBazaarSignIn(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.Map("/signin/{**rest}", HandleAsync).WithDisplayName("bazaar-signin");
        routes.Map("/signin", HandleAsync).WithDisplayName("bazaar-signin");
        routes.MapBazaarAuthHandoff();
        return routes;
    }

    private static async Task HandleAsync(HttpContext ctx)
    {
        var options = ctx.RequestServices.GetService<SignInOptions>() ?? SignInOptions.Default;
        var headers = ctx.Response.Headers;
        headers["Content-Security-Policy"] = ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";

        if (!options.Served)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = (ctx.Request.Path.Value ?? "/signin").TrimEnd('/');
        var method = ctx.Request.Method;
        switch (path.ToLowerInvariant())
        {
            case "/signin":
                if (IsRead(method))
                {
                    await ShellAsync(ctx, options);
                }
                else
                {
                    MethodNotAllowed(ctx, "GET, HEAD");
                }

                return;
            case "/signin/assets/signin.js":
            case "/signin/assets/signin.css":
                if (IsRead(method))
                {
                    await AssetAsync(ctx, path["/signin/".Length..]);
                }
                else
                {
                    MethodNotAllowed(ctx, "GET, HEAD");
                }

                return;
            case "/signin/challenge":
                if (HttpMethods.IsPost(method))
                {
                    await ChallengeAsync(ctx, options);
                }
                else
                {
                    MethodNotAllowed(ctx, "POST");
                }

                return;
            case "/signin/assert":
                if (HttpMethods.IsPost(method))
                {
                    await AssertAsync(ctx, options);
                }
                else
                {
                    MethodNotAllowed(ctx, "POST");
                }

                return;
            case "/signin/register":
            case "/signin/recover":
                // Pairing approval and recovery-code sign-in are separate work packages; the route is reserved and says nothing else.
                await ErrorAsync(ctx, StatusCodes.Status501NotImplemented, "Not available.");
                return;
            default:
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private static bool IsRead(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static void MethodNotAllowed(HttpContext ctx, string allow)
    {
        ctx.Response.Headers.Allow = allow;
        ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
    }

    private static async Task ShellAsync(HttpContext ctx, SignInOptions options)
    {
        // A page for an audience that is switched off is not served, so it cannot be used to probe settings other than "off".
        var audience = ctx.Request.Query.TryGetValue("audience", out var a) ? a.ToString() : SignInAudiences.Native;
        if (!options.Allows(audience))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!SignInAssets.TryGet("index.html", out var html, out var type))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var body = Encoding.UTF8.GetString(html).Replace("{{server-name}}", WebUtility.HtmlEncode(options.DisplayName), StringComparison.Ordinal);
        ctx.Response.ContentType = type;
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(body), ctx.RequestAborted);
        }
    }

    private static async Task AssetAsync(HttpContext ctx, string path)
    {
        if (!SignInAssets.TryGet(path, out var content, out var type))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = type;
        ctx.Response.Headers.CacheControl = "no-cache";
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.Body.WriteAsync(content, ctx.RequestAborted);
        }
    }

    /// <summary>A write needs a JSON content type and an <c>Origin</c> equal to the page origin, so another site cannot drive the ceremony.</summary>
    private static bool PassesWriteProtection(HttpRequest request)
    {
        var origin = request.Headers.Origin;
        return origin.Count == 1
            && string.Equals(origin[0], $"{request.Scheme}://{request.Host.Value}", StringComparison.OrdinalIgnoreCase)
            && MediaTypeHeaderValue.TryParse(request.ContentType, out var mt)
            && string.Equals(mt.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ChallengeAsync(HttpContext ctx, SignInOptions options)
    {
        if (!PassesWriteProtection(ctx.Request))
        {
            await GenericFailureAsync(ctx);
            return;
        }

        var store = ctx.RequestServices.GetRequiredService<IPasskeyChallengeStore>();
        var time = ctx.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        var challenge = await store.IssueAsync(time.GetUtcNow(), ctx.RequestAborted);
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(new ChallengeResponse(challenge.Id, challenge.Challenge, 60_000, options.RpId), SignInJson.Default.ChallengeResponse),
            ctx.RequestAborted);
    }

    private static async Task AssertAsync(HttpContext ctx, SignInOptions options)
    {
        var services = ctx.RequestServices;
        var time = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var now = time.GetUtcNow();

        // Every path below does the same work in the same order (consume the challenge, call the verifier) and ends in the same
        // response, so timing and body do not separate "no such user" from "wrong passkey" from "bad request".
        AssertRequest? request = null;
        if (PassesWriteProtection(ctx.Request))
        {
            request = await ReadBodyAsync(ctx);
        }

        var audience = ctx.Request.Query.TryGetValue("audience", out var q) ? q.ToString() : request?.Audience ?? SignInAudiences.Native;
        var challenge = await services.GetRequiredService<IPasskeyChallengeStore>().ConsumeAsync(request?.ChallengeId ?? string.Empty, now, ctx.RequestAborted);
        var identity = await services.GetRequiredService<IPasskeyAssertionVerifier>()
            .VerifyAsync(challenge ?? string.Empty, request?.Assertion ?? string.Empty, ctx.RequestAborted);

        if (challenge is null || identity is null || request is null || !options.Allows(audience))
        {
            await TryAuditAsync(services, now, "anonymous", "signin.failed", audience);
            await GenericFailureAsync(ctx);
            return;
        }

        var code = await services.GetRequiredService<IHandoffCodeStore>().IssueAsync(identity, audience, now, ctx.RequestAborted);
        var actor = identity.PrincipalId.ToString("D");
        if (services.GetService<IAuditLog>() is not null && !await TryAuditAsync(services, now, actor, "signin.handoff.issued", audience))
        {
            // The audit sink is present but refused the entry: fail closed rather than hand out an unrecorded credential.
            await GenericFailureAsync(ctx);
            return;
        }

        var escaped = Uri.EscapeDataString(code);
        var redirect = audience == SignInAudiences.Admin ? $"/admin/session/redeem?code={escaped}" : $"bazaar://signin?code={escaped}";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new RedirectResponse(redirect), SignInJson.Default.RedirectResponse), ctx.RequestAborted);
    }

    private static async Task<AssertRequest?> ReadBodyAsync(HttpContext ctx)
    {
        if (ctx.Request.ContentLength is > _maxBodyBytes)
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
                if (ms.Length + n > _maxBodyBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, n);
            }

            ms.Position = 0;
            return await JsonSerializer.DeserializeAsync(ms, SignInJson.Default.AssertRequest, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task GenericFailureAsync(HttpContext ctx) => ErrorAsync(ctx, StatusCodes.Status401Unauthorized, _genericError);

    private static async Task ErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new ErrorResponse(message), SignInJson.Default.ErrorResponse), ctx.RequestAborted);
    }

    /// <summary>Appends an audit entry when an audit log is registered. True when no log is registered or the append succeeded.</summary>
    private static async Task<bool> TryAuditAsync(IServiceProvider services, DateTimeOffset now, string actor, string kind, string audience)
    {
        if (services.GetService<IAuditLog>() is not { } log)
        {
            return true;
        }

        // The audience is only ever echoed when it is one of the two known constants.
        var safe = SignInAudiences.IsKnown(audience) ? audience : "unknown";
        try
        {
            await log.AppendAsync(new AuditEntry(now, actor, kind, $"{{\"audience\":\"{safe}\"}}"));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
