using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Approvals;

/// <summary>
/// <see cref="IDraftReviewDecisionSender"/> over the client's server endpoint: <c>POST /community/drafts/{id}/decision</c> with
/// the <see cref="DraftReviewDecisionDto"/> as JSON and the endpoint's bearer credential. It throws when the decision is
/// unsigned, when the server answers anything but a success, or when the server cannot be reached, so a failure is never silent.
/// The server route is a later slice (doc 40 section 4b).
/// </summary>
public sealed class HttpDraftReviewDecisionSender(HttpClient http, Func<string?> credential) : IDraftReviewDecisionSender
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>
    /// Creates a sender bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests; the sender owns the
    /// client. A re-pair creates a new sender with the new endpoint.
    /// </summary>
    public static HttpDraftReviewDecisionSender Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpDraftReviewDecisionSender(http, () => endpoint.Credential);
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The decision is unsigned or its approval is for another draft.</exception>
    /// <exception cref="HttpRequestException">The server refused the decision or could not be reached.</exception>
    public async Task SendAsync(DraftReviewDecisionDto decision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (string.IsNullOrEmpty(decision.Decision.SignatureBase64))
        {
            throw new InvalidOperationException("An unsigned decision is never sent.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, CommunityRoutes.DraftDecision(decision.DraftId))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(decision, CommunityContractsJsonContext.Default.DraftReviewDecisionDto)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var token = _credential();
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
