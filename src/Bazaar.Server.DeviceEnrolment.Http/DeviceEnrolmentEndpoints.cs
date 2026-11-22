using System.Globalization;
using System.Text.Json;
using Bazaar.Approvals;
using Bazaar.Contracts.Enrolment;
using Bazaar.Server.Biometric;
using Bazaar.Server.Wire.Connection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.DeviceEnrolment.Http;

/// <summary>
/// The enrolment routes of doc 32 section 5b (PROPOSED): <c>POST /enrol/challenge</c> and <c>POST /enrol/complete</c>. The human
/// id and the device id come from the access token, never from the body. Only the owner may enrol (a coworker device enrols no
/// approval key unless the owner delegated a scope, doc 32 5a). The ceremony itself, and every check in it, is
/// <see cref="IDeviceEnrolment"/>; this layer maps the wire to it and checks that the request names the signed-in device.
/// </summary>
public static class DeviceEnrolmentEndpoints
{
    private const int _maxBodyBytes = 128 * 1024;
    private const string _ownerKind = "owner";

    /// <summary>Maps both routes. The authenticator, the ceremony and the audit are looked up per request, so a missing one answers 503.</summary>
    public static IEndpointRouteBuilder MapBazaarDeviceEnrolmentRoutes(this IEndpointRouteBuilder routes)
    {
        routes.MapPost(EnrolmentRoutes.Challenge, Route(ChallengeAsync));
        routes.MapPost(EnrolmentRoutes.Complete, Route(CompleteAsync));
        return routes;
    }

    // A RequestDelegate keeps the routes free of reflection (the project is AOT compatible).
    private static RequestDelegate Route(Func<HttpContext, Caller, IDeviceEnrolment, Task<IResult>> run) =>
        async http => await (await GuardedAsync(http, run).ConfigureAwait(false)).ExecuteAsync(http).ConfigureAwait(false);

    private sealed record Caller(Guid HumanId, Guid DeviceId);

    private static async Task<IResult> GuardedAsync(HttpContext http, Func<HttpContext, Caller, IDeviceEnrolment, Task<IResult>> run)
    {
        var services = http.RequestServices;
        var wire = services.GetRequiredService<IOptions<WireOptions>>().Value;
        var remote = http.Connection.RemoteIpAddress;
        if (!http.Request.IsHttps && !wire.AllowPlaintextOverlay && remote is not null && !System.Net.IPAddress.IsLoopback(remote))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }

        var authenticator = services.GetService<IConnectionAuthenticator>();
        var enrolment = services.GetService<IDeviceEnrolment>();
        if (authenticator is null || enrolment is null)
        {
            return Error(StatusCodes.Status503ServiceUnavailable, "unavailable");
        }

        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        if (string.IsNullOrEmpty(token))
        {
            return Unauthorized(http);
        }

        var auth = await authenticator.AuthenticateAsync(new HelloCredential(token), new DeviceInfo("http", "http", string.Empty), http.RequestAborted).ConfigureAwait(false);
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        if (!auth.Ok || auth.Principal is null || (auth.ExpiresAt is { } expiry && expiry <= now))
        {
            return Unauthorized(http);
        }

        if (!string.Equals(auth.Principal.Kind, _ownerKind, StringComparison.Ordinal))
        {
            return Error(StatusCodes.Status403Forbidden, "forbidden");
        }

        // The device id is the server-assigned one in the token. Without it the request cannot be tied to a device.
        if (!Guid.TryParseExact(auth.DeviceId, "D", out var deviceId))
        {
            return Error(StatusCodes.Status403Forbidden, "device_unknown");
        }

        return await run(http, new Caller(auth.Principal.Id, deviceId), enrolment).ConfigureAwait(false);
    }

    private static async Task<IResult> ChallengeAsync(HttpContext http, Caller caller, IDeviceEnrolment enrolment)
    {
        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var challenge = await enrolment.BeginAsync(caller.HumanId, caller.DeviceId, now, http.RequestAborted).ConfigureAwait(false);
        return Json(new EnrolmentChallengeDto(Convert.ToBase64String(challenge.Nonce), challenge.ExpiresAt), StatusCodes.Status200OK, EnrolmentContractsJsonContext.Default.EnrolmentChallengeDto);
    }

    private static async Task<IResult> CompleteAsync(HttpContext http, Caller caller, IDeviceEnrolment enrolment)
    {
        if (http.Request.ContentLength is > _maxBodyBytes)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, "too_large");
        }

        var body = await ReadBodyAsync(http).ConfigureAwait(false);
        if (body is null)
        {
            return Error(StatusCodes.Status400BadRequest, "malformed");
        }

        var now = http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        if (body.DeviceId != caller.DeviceId)
        {
            var audit = http.RequestServices.GetService<IEnrolmentAudit>();
            if (audit is not null)
            {
                await audit.RefusedAsync(caller.HumanId, caller.DeviceId, EnrolmentVerdict.DeviceMismatch, now, http.RequestAborted).ConfigureAwait(false);
            }

            return Refusal(EnrolmentVerdict.DeviceMismatch);
        }

        var request = Map(body, caller);
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "malformed");
        }

        var outcome = await enrolment.CompleteAsync(request, now, http.RequestAborted).ConfigureAwait(false);
        return outcome.Succeeded
            ? Json(new EnrolmentReplyDto(true, Reason(outcome.Verdict), outcome.CredentialAttested), StatusCodes.Status200OK, EnrolmentContractsJsonContext.Default.EnrolmentReplyDto)
            : Refusal(outcome.Verdict);
    }

    private static async Task<EnrolmentCompleteDto?> ReadBodyAsync(HttpContext http)
    {
        try
        {
            using var limited = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await http.Request.Body.ReadAsync(buffer, http.RequestAborted).ConfigureAwait(false)) > 0)
            {
                if (limited.Length + read > _maxBodyBytes)
                {
                    return null;
                }

                limited.Write(buffer, 0, read);
            }

            limited.Position = 0;
            return await JsonSerializer.DeserializeAsync(limited, EnrolmentContractsJsonContext.Default.EnrolmentCompleteDto, http.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EnrolmentRequest? Map(EnrolmentCompleteDto d, Caller caller)
    {
        if (!TryClass(d.DeviceClass, out var cls)
            || !TryB64(d.Nonce, out var nonce)
            || !TryB64(d.ApprovalPublicKey, out var key)
            || d.ApprovalKeyAttestation is null
            || !TryB64(d.CredentialId, out var credentialId)
            || !TryB64(d.CredentialAttestation, out var credentialAttestation)
            || !TryB64(d.CredentialClientData, out var clientData)
            || (d.ConsoleCode is null) == (d.Approval is null))
        {
            return null;
        }

        var chain = new List<byte[]>(d.ApprovalKeyAttestation.Count);
        foreach (var element in d.ApprovalKeyAttestation)
        {
            if (!TryB64(element, out var blob))
            {
                return null;
            }

            chain.Add(blob);
        }

        EnrolmentStarter? starter = d.ConsoleCode is { } code ? new ConsoleCodeStarter(code) : MapApproval(d.Approval!);
        return starter is null
            ? null
            : new EnrolmentRequest(caller.HumanId, caller.DeviceId, cls, nonce, key, chain, credentialId, credentialAttestation, clientData, starter);
    }

    private static EnrolledDeviceStarter? MapApproval(EnrolmentApprovalDto a)
    {
        var b = a.Biometric;
        if (b is null
            || !TryB64(a.ArgsHash, out var args)
            || !TryB64(a.Nonce, out var nonce)
            || !TryB64(a.Signature, out var signature)
            || !TryB64(b.CredentialId, out var credentialId)
            || !TryB64(b.AuthenticatorData, out var authData)
            || !TryB64(b.ClientDataJson, out var clientData)
            || !TryB64(b.Signature, out var bioSignature))
        {
            return null;
        }

        var payload = new OwnerPayload(a.Action, a.ApprovalId, args, null, a.HumanId, a.DeviceId, a.IssuedAt, a.ExpiresAt, nonce);
        return new EnrolledDeviceStarter(payload, signature, new BiometricAssertion(b.Version, credentialId, authData, clientData, bioSignature));
    }

    private static bool TryClass(string? value, out DeviceClass cls)
    {
        (var ok, cls) = value switch
        {
            "windows" => (true, DeviceClass.Windows),
            "linux" => (true, DeviceClass.Linux),
            "android" => (true, DeviceClass.Android),
            "terminal" => (true, DeviceClass.Terminal),
            "other" => (true, DeviceClass.Other),
            _ => (false, default(DeviceClass)),
        };
        return ok;
    }

    private static bool TryB64(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null)
        {
            return false;
        }

        var buffer = new byte[value.Length * 3 / 4 + 3];
        if (!Convert.TryFromBase64String(value, buffer, out var written))
        {
            return false;
        }

        bytes = buffer.AsSpan(0, written).ToArray();
        return true;
    }

    /// <summary>The fixed reason code of a verdict, as snake_case.</summary>
    internal static string Reason(EnrolmentVerdict verdict) => verdict switch
    {
        EnrolmentVerdict.Enrolled => "enrolled",
        EnrolmentVerdict.Malformed => "malformed",
        EnrolmentVerdict.ChallengeInvalid => "challenge_invalid",
        EnrolmentVerdict.StarterRefused => "starter_refused",
        EnrolmentVerdict.DeviceUnknown => "device_unknown",
        EnrolmentVerdict.AlreadyEnrolled => "already_enrolled",
        EnrolmentVerdict.ApprovalKeyShape => "approval_key_shape",
        EnrolmentVerdict.ApprovalKeyNotHardware => "approval_key_not_hardware",
        EnrolmentVerdict.CredentialRefused => "credential_refused",
        EnrolmentVerdict.CredentialSynced => "credential_synced",
        EnrolmentVerdict.DeviceMismatch => "device_mismatch",
        _ => "refused",
    };

    private static IResult Refusal(EnrolmentVerdict verdict) =>
        verdict == EnrolmentVerdict.Malformed
            ? Error(StatusCodes.Status400BadRequest, "malformed")
            : Error(verdict == EnrolmentVerdict.AlreadyEnrolled ? StatusCodes.Status409Conflict : StatusCodes.Status403Forbidden, Reason(verdict));

    private static IResult Unauthorized(HttpContext http)
    {
        http.Response.Headers.WWWAuthenticate = "Bearer";
        return Error(StatusCodes.Status401Unauthorized, "unauthorized");
    }

    private static IResult Json<T>(T value, int status, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) =>
        Results.Json(value, info, statusCode: status);

    private static IResult Error(int status, string reason) =>
        Results.Json(new EnrolmentReplyDto(false, reason, false), EnrolmentContractsJsonContext.Default.EnrolmentReplyDto, statusCode: status);
}
