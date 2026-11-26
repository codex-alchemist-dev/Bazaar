using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Approvals;
using Bazaar.Server.Council.Hiring;
using Bazaar.Server.TalentPool.Component;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.TalentPool.Http;

/// <summary>One tool grant waiting for the owner. Same shape the phone reads.</summary>
public sealed record ToolGrantCardDto(string TemplateId, int Version, string Name, string[] Tools);

/// <summary>The owner-signed grant as the phone sends it (hex and base64 text).</summary>
public sealed record ToolGrantDecisionDto(
    string Action,
    Guid ApprovalId,
    string ArgsHashHex,
    Guid HumanId,
    string DeviceId,
    long IssuedAtUnixMs,
    long ExpiresAtUnixMs,
    string NonceHex,
    string SignatureBase64);

/// <summary>Source-generated JSON for the routes.</summary>
[JsonSerializable(typeof(ToolGrantCardDto[]))]
[JsonSerializable(typeof(ToolGrantDecisionDto))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TalentRoutesJson : JsonSerializerContext;

internal sealed record ErrorBody(string Error);

/// <summary>
/// The Talent tool-grant routes (docs/design/25, doc 29 Approvals; routes PROPOSED). Host composition passes the HTTP surface guard
/// (bearer, rate bucket) as <c>guard</c>, so this module never references the server project. The POST is also checked by
/// <see cref="TalentToolGrantApprover"/>: an owner signature over the exact template version and tool list, so a bearer token alone
/// approves nothing.
/// <list type="bullet">
/// <item><c>GET /talent/tool-grants/pending</c>: released templates whose tools have no approval yet.</item>
/// <item><c>POST /talent/templates/{id}/versions/{version}/tool-grant</c>: 204 recorded; 400 bad input or a payload that does not
/// cover this version's tools; 403 signature refused (no detail); 404 no such version or it asks for no tools.</item>
/// </list>
/// </summary>
public static class TalentToolGrantRoutes
{
    private const int _maxBodyBytes = 4 * 1024;

    /// <summary>Maps both routes.</summary>
    public static IEndpointRouteBuilder MapBazaarTalentToolGrants(this IEndpointRouteBuilder app, Func<RouteHandlerBuilder, RouteHandlerBuilder> guard)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(guard);
        guard(app.MapGet("/talent/tool-grants/pending", (Func<HttpContext, Task<IResult>>)PendingAsync));
        guard(app.MapPost("/talent/templates/{id}/versions/{version}/tool-grant", (Func<string, string, HttpContext, Task<IResult>>)GrantAsync));
        return app;
    }

    internal static async Task<IResult> PendingAsync(HttpContext http)
    {
        // Reads the store and the hire state directly: listing pending grants must not need the Council service (the host wires it later).
        var templates = http.RequestServices.GetRequiredService<ITalentTemplateStore>();
        var hires = http.RequestServices.GetRequiredService<IHireDecisionStore>();
        var approvals = http.RequestServices.GetRequiredService<ITalentToolGrantApproval>();
        var ct = http.RequestAborted;
        var cards = new List<ToolGrantCardDto>();
        foreach (var t in await templates.ListLatestAsync(ct).ConfigureAwait(false))
        {
            if (t.Manifest.ToolAllowlist.Count == 0
                || await hires.FindAsync(t.HireKey, ct).ConfigureAwait(false) is not { Status: HireStatus.Released })
            {
                continue;
            }

            if (!await approvals.IsApprovedAsync(t.HireKey, t.Manifest.ToolAllowlist, ct).ConfigureAwait(false))
            {
                cards.Add(new ToolGrantCardDto(t.TemplateId, t.Version, t.Manifest.Name, [.. t.Manifest.ToolAllowlist]));
            }
        }

        return Results.Json(cards.ToArray(), TalentRoutesJson.Default.ToolGrantCardDtoArray);
    }

    internal static async Task<IResult> GrantAsync(string id, string version, HttpContext http)
    {
        if (!int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 1 || id.Length is 0 or > 128)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        // The phone's JsonContent is sent chunked, so there may be no Content-Length: read at most the limit plus one byte.
        var buffer = new byte[_maxBodyBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await http.Request.Body.ReadAsync(buffer.AsMemory(read), http.RequestAborted).ConfigureAwait(false)) > 0)
        {
            read += n;
        }

        if (read is 0 or > _maxBodyBytes)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        ToolGrantDecisionDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(buffer.AsSpan(0, read), TalentRoutesJson.Default.ToolGrantDecisionDto);
        }
        catch (JsonException)
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        if (dto is null || !TryBuild(dto, out var payload, out var signature))
        {
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }

        var approver = http.RequestServices.GetRequiredService<TalentToolGrantApprover>();
        try
        {
            return await approver.ApproveAsync(id, v, payload, signature, http.RequestAborted).ConfigureAwait(false) switch
            {
                ToolGrantApprovalResult.Approved => Results.NoContent(),
                ToolGrantApprovalResult.NothingToApprove => Error(StatusCodes.Status404NotFound, "not_found"),
                ToolGrantApprovalResult.PayloadMismatch => Error(StatusCodes.Status400BadRequest, "bad_request"),
                _ => Error(StatusCodes.Status403Forbidden, "grant_not_approved"),
            };
        }
        catch (ArgumentException)
        {
            // The payload has a field the signed layout does not allow, so nothing was verified or recorded.
            return Error(StatusCodes.Status400BadRequest, "bad_request");
        }
    }

    private static bool TryBuild(ToolGrantDecisionDto dto, out OwnerPayload payload, out byte[] signature)
    {
        payload = null!;
        signature = [];
        try
        {
            var hash = Convert.FromHexString(dto.ArgsHashHex);
            var nonce = Convert.FromHexString(dto.NonceHex);
            signature = Convert.FromBase64String(dto.SignatureBase64);
            if (nonce.Length != OwnerSignedPayload.NonceLength || signature.Length is 0 or > 128 || string.IsNullOrEmpty(dto.Action) || string.IsNullOrEmpty(dto.DeviceId))
            {
                return false;
            }

            payload = new OwnerPayload(
                dto.Action,
                dto.ApprovalId,
                hash,
                null,
                dto.HumanId,
                dto.DeviceId,
                DateTimeOffset.FromUnixTimeMilliseconds(dto.IssuedAtUnixMs),
                DateTimeOffset.FromUnixTimeMilliseconds(dto.ExpiresAtUnixMs),
                nonce);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static IResult Error(int status, string code) => Results.Json(new ErrorBody(code), TalentRoutesJson.Default.ErrorBody, statusCode: status);
}
