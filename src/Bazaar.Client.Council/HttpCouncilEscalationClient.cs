using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;

namespace Bazaar.Client.Council;

/// <summary>
/// The server link for the owner's Council escalations (docs/design/10, Escalation; routes PROPOSED, same bearer link as
/// <c>HttpApprovalClient</c>). <c>GET council/escalations</c> lists, <c>POST council/escalations/{voteId}/resolve</c> resolves.
/// A failed resolve throws <see cref="CouncilEscalationException"/> and is never retried.
/// </summary>
public sealed class HttpCouncilEscalationClient(HttpClient http, Func<string?> credential) : ICouncilEscalationClient
{
    /// <summary>Creates a client bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpCouncilEscalationClient Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var client = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpCouncilEscalationClient(client, () => endpoint.Credential);
    }

    public async ValueTask<IReadOnlyList<EscalationCard>> ListEscalationsAsync(CancellationToken cancellationToken)
    {
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Get, "council/escalations"));
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
        var list = await response.Content.ReadFromJsonAsync(CouncilEscalationJsonContext.Default.EscalationListDto, cancellationToken).ConfigureAwait(false);
        return list?.Escalations ?? [];
    }

    public async ValueTask ResolveEscalationAsync(Guid voteId, string choice, string? reasoning, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(choice);
        var route = string.Create(CultureInfo.InvariantCulture, $"council/escalations/{voteId}/resolve");
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Post, route)
        {
            Content = JsonContent.Create(new ResolveEscalationRequest(choice, reasoning), CouncilEscalationJsonContext.Default.ResolveEscalationRequest),
        });
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            code = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
        }
        catch (JsonException)
        {
        }

        throw new CouncilEscalationException((int)response.StatusCode, code);
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
