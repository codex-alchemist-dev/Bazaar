using System.Net.Http.Headers;
using System.Net.Http.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Approvals;

namespace Bazaar.Client.Approvals.Http;

/// <summary>
/// The phone's server link for approvals (doc 29 Approvals, routes PROPOSED): <c>GET /approvals/pending</c> returns the
/// pending cards, and <c>POST /approvals/{id}/decision</c> takes one signed decision. Both ride the authenticated
/// connection with the bearer credential; nothing is cached on the device. A failed send throws, and the gate never
/// retries a signed decision.
/// </summary>
public sealed class HttpApprovalClient(HttpClient http, Func<string?> credential) : IPendingApprovalSource, IApprovalDecisionSender
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Creates a client bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpApprovalClient Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpApprovalClient(http, () => endpoint.Credential);
    }

    public async ValueTask<IReadOnlyList<ApprovalCardDto>> ListPendingAsync(CancellationToken cancellationToken)
    {
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Get, "approvals/pending"));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var cards = await response.Content
            .ReadFromJsonAsync(ApprovalContractsJsonContext.Default.ApprovalCardDtoArray, cancellationToken)
            .ConfigureAwait(false);
        return cards ?? [];
    }

    public async ValueTask SendAsync(ApprovalDecisionDto decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        using var request = Authorized(new HttpRequestMessage(HttpMethod.Post, $"approvals/{decision.ApprovalId:D}/decision")
        {
            Content = JsonContent.Create(decision, ApprovalContractsJsonContext.Default.ApprovalDecisionDto),
        });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Authorized(HttpRequestMessage request)
    {
        var token = _credential();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }
}
