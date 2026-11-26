using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.SignIn.WebAuthn.Credentials;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;

namespace Bazaar.Server.SignIn.WebAuthn.Registration;

/// <summary>
/// The passkey registration ceremony under <c>/signin/register</c> (doc 32 section 4), replacing the sign-in module's 501 for
/// <c>POST</c>. Two steps: <c>POST /signin/register/begin</c> authorises the caller and issues a challenge, and
/// <c>POST /signin/register</c> verifies the attestation and stores the public key. Only two callers may register: a human who is
/// already signed in (through <see cref="IRegistrationPrincipalResolver"/>), or the operator holding the one-time bootstrap code,
/// from loopback, for a principal that has no passkey yet. Every refusal answers the same 401 body. Nothing is stored unless the
/// audit log accepts the entry.
/// </summary>
public static class RegistrationEndpoints
{
    private const int _maxBodyBytes = 32 * 1024;
    private const string _genericError = "Registration failed.";

    public static IEndpointRouteBuilder MapBazaarSignInWebAuthn(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        routes.MapPost("/signin/register/begin", BeginAsync).WithDisplayName("bazaar-signin-register-begin");
        routes.MapPost("/signin/register", FinishAsync).WithDisplayName("bazaar-signin-register");
        return routes;
    }

    private static async Task BeginAsync(HttpContext ctx)
    {
        SetHeaders(ctx);
        var services = ctx.RequestServices;
        var signIn = services.GetService<SignInOptions>() ?? SignInOptions.Default;
        if (!signIn.Served)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var options = services.GetRequiredService<WebAuthnOptions>();
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow();
        var request = await ReadBodyAsync(ctx, options, RegistrationJson.Default.BeginRequest);
        var principal = request is null || !options.IsConfigured || services.GetService<IAuditLog>() is null
            ? null
            : await AuthorizeBeginAsync(ctx, options, request.BootstrapCode);

        if (request is null || principal is not { } who || SanitizeLabel(request.Label) is not { } label)
        {
            await DeniedAsync(ctx, now, "begin");
            return;
        }

        var challenge = services.GetRequiredService<RegistrationChallengeStore>().Issue(who.PrincipalId, label, who.ViaBootstrap, now);
        var existing = await services.GetRequiredService<IPasskeyCredentialStore>().ListCredentialIdsAsync(who.PrincipalId, ctx.RequestAborted);
        var response = new BeginResponse(
            challenge.Id,
            challenge.Challenge,
            (int)RegistrationChallengeStore.Lifetime.TotalMilliseconds,
            new RpInfo(options.RpId!, signIn.DisplayName),
            new UserInfo(WebAuthnWire.Encode(who.PrincipalId.ToByteArray(bigEndian: true)), who.PrincipalId.ToString("D"), label),
            [new CredentialParam("public-key", -7)],
            new AuthenticatorSelection("required", "required"),
            "none",
            [.. existing.Select(id => WebAuthnWire.Encode(id))]);
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, response, RegistrationJson.Default.BeginResponse);
    }

    private static async Task FinishAsync(HttpContext ctx)
    {
        SetHeaders(ctx);
        var services = ctx.RequestServices;
        var signIn = services.GetService<SignInOptions>() ?? SignInOptions.Default;
        if (!signIn.Served)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var options = services.GetRequiredService<WebAuthnOptions>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var audit = services.GetService<IAuditLog>();
        var request = await ReadBodyAsync(ctx, options, RegistrationJson.Default.FinishRequest);

        // The challenge is consumed first on every path, so a guessed id cannot be probed twice.
        var challenge = services.GetRequiredService<RegistrationChallengeStore>().Consume(request?.ChallengeId, now);
        var verified = challenge is null || request?.Attestation is null || !options.IsConfigured || audit is null
            ? null
            : services.GetRequiredService<AttestationVerifier>().Verify(challenge.Challenge, request.Attestation);

        if (challenge is null || verified is null || !await StillAuthorizedAsync(ctx, options, challenge))
        {
            await DeniedAsync(ctx, now, "finish");
            return;
        }

        var store = services.GetRequiredService<IPasskeyCredentialStore>();
        var credential = new StoredPasskeyCredential(
            Guid.CreateVersion7(now),
            challenge.PrincipalId,
            Guid.CreateVersion7(now),
            verified.CredentialId,
            verified.PublicKey,
            verified.SignCount,
            challenge.Label,
            now);
        if (!await store.AddAsync(credential, ctx.RequestAborted))
        {
            await DeniedAsync(ctx, now, "finish");
            return;
        }

        // Ids only: no key, attestation or credential id is written to the audit log or any other log.
        var via = challenge.ViaBootstrap ? "bootstrap" : "session";
        try
        {
            await audit!.AppendAsync(
                new AuditEntry(now, challenge.PrincipalId.ToString("D"), "signin.passkey.registered", $"{{\"credential\":\"{credential.Id:D}\",\"via\":\"{via}\"}}"),
                ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An unrecorded credential is not kept: revoke it and fail closed.
            await store.RevokeAsync(credential.Id, now, CancellationToken.None);
            WebAuthnLog.RegistrationRolledBack(
                services.GetService<ILoggerFactory>()?.CreateLogger("Bazaar.Server.SignIn.WebAuthn") ?? NullLogger.Instance,
                ex.GetType().Name);
            await GenericFailureAsync(ctx);
            return;
        }

        await WriteJsonAsync(ctx, StatusCodes.Status200OK, new RegisteredResponse(true, credential.Id.ToString("D")), RegistrationJson.Default.RegisteredResponse);
    }

    private readonly record struct Authorized(Guid PrincipalId, bool ViaBootstrap);

    private static async Task<Authorized?> AuthorizeBeginAsync(HttpContext ctx, WebAuthnOptions options, string? bootstrapCode)
    {
        var services = ctx.RequestServices;
        if (await services.GetRequiredService<IRegistrationPrincipalResolver>().ResolveAsync(ctx, ctx.RequestAborted) is { } signedIn)
        {
            return new Authorized(signedIn, false);
        }

        if (string.IsNullOrEmpty(bootstrapCode) || !BootstrapAllowed(ctx, options, out var principalId))
        {
            return null;
        }

        var count = await services.GetRequiredService<IPasskeyCredentialStore>().CountAsync(principalId, ctx.RequestAborted);
        return count == 0 && services.GetRequiredService<BootstrapCodeService>().Verify(bootstrapCode, principalId)
            ? new Authorized(principalId, true)
            : null;
    }

    private static async Task<bool> StillAuthorizedAsync(HttpContext ctx, WebAuthnOptions options, RegistrationChallenge challenge)
    {
        var services = ctx.RequestServices;
        if (!challenge.ViaBootstrap)
        {
            return await services.GetRequiredService<IRegistrationPrincipalResolver>().ResolveAsync(ctx, ctx.RequestAborted) == challenge.PrincipalId;
        }

        return BootstrapAllowed(ctx, options, out var principalId)
            && principalId == challenge.PrincipalId
            && services.GetRequiredService<BootstrapCodeService>().TryConsume(principalId);
    }

    /// <summary>The bootstrap path needs the operator's switch on, a named principal, and a loopback caller.</summary>
    private static bool BootstrapAllowed(HttpContext ctx, WebAuthnOptions options, out Guid principalId)
    {
        principalId = options.BootstrapPrincipalId ?? Guid.Empty;
        return options.BootstrapEnabled
            && options.BootstrapPrincipalId is not null
            && ctx.Connection.RemoteIpAddress is { } ip
            && System.Net.IPAddress.IsLoopback(ip);
    }

    internal static string? SanitizeLabel(string? label)
    {
        var trimmed = string.IsNullOrWhiteSpace(label) ? "Passkey" : label.Trim();
        return trimmed.Length <= WebAuthnOptions.MaxLabelLength && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    private static void SetHeaders(HttpContext ctx)
    {
        var headers = ctx.Response.Headers;
        headers["Content-Security-Policy"] = SignInEndpoints.ContentSecurityPolicy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Cache-Control"] = "no-store";
    }

    /// <summary>A write needs a JSON content type and an <c>Origin</c> that is one of the operator's allowed origins.</summary>
    private static bool PassesWriteProtection(HttpRequest request, WebAuthnOptions options)
    {
        var origin = request.Headers.Origin;
        return origin.Count == 1
            && options.IsOriginAllowed(origin[0])
            && MediaTypeHeaderValue.TryParse(request.ContentType, out var mt)
            && string.Equals(mt.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpContext ctx, WebAuthnOptions options, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        where T : class
    {
        if (!PassesWriteProtection(ctx.Request, options) || ctx.Request.ContentLength is > _maxBodyBytes)
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
            return await JsonSerializer.DeserializeAsync(ms, info, ctx.RequestAborted);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DeniedAsync(HttpContext ctx, DateTimeOffset now, string step)
    {
        if (ctx.RequestServices.GetService<IAuditLog>() is { } log)
        {
            try
            {
                await log.AppendAsync(new AuditEntry(now, "anonymous", "signin.passkey.register.denied", $"{{\"step\":\"{step}\"}}"), ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The refusal stands whether or not it could be recorded.
            }
        }

        await GenericFailureAsync(ctx);
    }

    private static Task GenericFailureAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status401Unauthorized, new ErrorBody(_genericError), RegistrationJson.Default.ErrorBody);

    private static async Task WriteJsonAsync<T>(HttpContext ctx, int status, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.Body.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(value, info), ctx.RequestAborted);
    }
}

internal sealed record BeginRequest(string? BootstrapCode, string? Label);

internal sealed record FinishRequest(string? ChallengeId, string? Attestation);

internal sealed record RpInfo(string Id, string Name);

internal sealed record UserInfo(string Id, string Name, string DisplayName);

internal sealed record CredentialParam(string Type, int Alg);

internal sealed record AuthenticatorSelection(string ResidentKey, string UserVerification);

internal sealed record BeginResponse(
    string Id,
    string Challenge,
    int TimeoutMs,
    RpInfo Rp,
    UserInfo User,
    CredentialParam[] PubKeyCredParams,
    AuthenticatorSelection AuthenticatorSelection,
    string Attestation,
    string[] ExcludeCredentials);

internal sealed record RegisteredResponse(bool Registered, string CredentialRef);

internal sealed record ErrorBody(string Error);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BeginRequest))]
[JsonSerializable(typeof(FinishRequest))]
[JsonSerializable(typeof(BeginResponse))]
[JsonSerializable(typeof(RegisteredResponse))]
[JsonSerializable(typeof(ErrorBody))]
internal sealed partial class RegistrationJson : JsonSerializerContext;
