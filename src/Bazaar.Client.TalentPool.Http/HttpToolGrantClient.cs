using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Bazaar.Approvals;
using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.TalentPool.Http;

/// <summary>The signed grant as it goes over the wire. Hex and base64 text, so the server rebuilds the exact <c>OwnerPayload</c>.</summary>
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

/// <summary>Source-generated JSON for the Talent tool-grant wire types.</summary>
[JsonSerializable(typeof(ToolGrantCard[]))]
[JsonSerializable(typeof(ToolGrantDecisionDto))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public sealed partial class ToolGrantJsonContext : JsonSerializerContext;

/// <summary>
/// The phone's server link for Talent tool grants (docs/design/25, doc 29 Approvals; routes PROPOSED, same bearer link as
/// <c>HttpApprovalClient</c>). <c>GET talent/tool-grants/pending</c> lists the cards. <c>POST
/// talent/templates/{id}/versions/{version}/tool-grant</c> takes one owner-signed grant. A failed send throws and is never retried.
/// </summary>
public sealed class HttpToolGrantClient(HttpClient http, Func<string?> credential) : IPendingToolGrantSource, IToolGrantSender
{
    /// <summary>Creates a client bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpToolGrantClient Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpToolGrantClient(client, () => endpoint.Credential);
    }

    public async ValueTask<IReadOnlyList<ToolGrantCard>> ListPendingAsync(CancellationToken cancellationToken)
    {
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Get, "talent/tool-grants/pending"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var cards = await response.Content.ReadFromJsonAsync(ToolGrantJsonContext.Default.ToolGrantCardArray, cancellationToken).ConfigureAwait(false);
        return cards ?? [];
    }

    public async ValueTask SendAsync(string templateId, int version, OwnerPayload payload, byte[] signature, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var dto = new ToolGrantDecisionDto(
            payload.Action,
            payload.ApprovalId,
            Convert.ToHexStringLower(payload.ArgsHash),
            payload.HumanId,
            payload.DeviceId,
            payload.IssuedAt.ToUnixTimeMilliseconds(),
            payload.ExpiresAt.ToUnixTimeMilliseconds(),
            Convert.ToHexStringLower(payload.Nonce),
            Convert.ToBase64String(signature));
        var route = string.Create(CultureInfo.InvariantCulture, $"talent/templates/{Uri.EscapeDataString(templateId)}/versions/{version}/tool-grant");
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(dto, ToolGrantJsonContext.Default.ToolGrantDecisionDto),
        });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Authorized(HttpRequestMessage request)
    {
        if (credential() is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
