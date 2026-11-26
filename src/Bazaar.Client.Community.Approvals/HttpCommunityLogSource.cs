using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Client.Abstractions.Server;
using Bazaar.Contracts.Community;

namespace Bazaar.Client.Community.Approvals;

/// <summary>
/// <see cref="ICommunityLogSource"/> over the client's server endpoint: <c>GET /community/log?agentId&amp;platform&amp;limit&amp;cursor</c>
/// with the endpoint's bearer credential (doc 36 WP9). The cursor is the server's opaque keyset cursor and is passed through
/// untouched. A refusal or an unreachable server throws <see cref="HttpRequestException"/> with a short message that the log
/// pane shows; the response body is never echoed. [PROPOSED, 2026-11-22]
/// </summary>
public sealed class HttpCommunityLogSource(HttpClient http, Func<string?> credential) : ICommunityLogSource
{
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly Func<string?> _credential = credential ?? throw new ArgumentNullException(nameof(credential));

    /// <summary>Creates a source bound to <paramref name="endpoint"/>. The <paramref name="handler"/> is for tests.</summary>
    public static HttpCommunityLogSource Create(ServerEndpoint endpoint, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var http = handler is null ? endpoint.CreateHttpClient() : new HttpClient(handler) { BaseAddress = endpoint.BaseAddress };
        return new HttpCommunityLogSource(http, () => endpoint.Credential);
    }

    /// <summary>Builds the relative request URL; blank filters and a blank cursor are left out, values are escaped.</summary>
    public static string BuildRelativeUrl(CommunityLogQueryDto query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = new StringBuilder(CommunityRoutes.Log);
        var separator = '?';

        void Add(string name, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            url.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        Add("agentId", query.AgentId);
        Add("platform", query.Platform);
        Add("limit", query.Limit.ToString(CultureInfo.InvariantCulture));
        Add("cursor", query.Cursor);
        return url.ToString();
    }

    /// <inheritdoc />
    /// <exception cref="HttpRequestException">The server refused the request, answered badly, or could not be reached.</exception>
    public async Task<CommunityLogPageDto> LoadAsync(CommunityLogQueryDto query, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, BuildRelativeUrl(query));
        var token = _credential();
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(Describe(response.StatusCode), null, response.StatusCode);
        }

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var page = await JsonSerializer.DeserializeAsync(stream, CommunityContractsJsonContext.Default.CommunityLogPageDto, cancellationToken).ConfigureAwait(false);
                return page ?? throw new HttpRequestException("The server sent an empty community log answer.", null, response.StatusCode);
            }
        }
        catch (JsonException)
        {
            throw new HttpRequestException("The server sent a community log answer this client cannot read.", null, response.StatusCode);
        }
    }

    private static string Describe(HttpStatusCode status) => (int)status switch
    {
        401 => "The server did not accept this client's credential (401). Pair the client again.",
        403 => "Only the owner may read the community log (403).",
        404 => "The server has no community log route (404). It may be an older server.",
        429 => "The server is rate limiting this client (429). Try again shortly.",
        >= 500 => $"The server could not serve the community log ({(int)status}).",
        _ => $"The server refused the community log request ({(int)status}).",
    };
}
