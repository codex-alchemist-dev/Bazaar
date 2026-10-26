using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters.Common;

namespace Bazaar.Server.Gateway.Adapters.Anthropic;

/// <summary>
/// The Anthropic Messages wire family (<c>POST {BaseUrl}/v1/messages</c>, streamed). It serves the owner's direct key and any
/// operator-configured Anthropic-compatible endpoint. Upstream problems never throw: they arrive as a
/// <see cref="ModelEvent.Failure"/>. The caller's cancellation does throw, as usual.
/// </summary>
public sealed class AnthropicMessagesAdapter : IModelAdapter
{
    public const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;

    /// <param name="http">The HTTP client. Its <see cref="HttpClient.Timeout"/> is what produces <c>timeout</c> failures.</param>
    public AnthropicMessagesAdapter(HttpClient http) => _http = http ?? throw new ArgumentNullException(nameof(http));

    public AdapterKind Kind => AdapterKind.AnthropicMessages;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ProviderEndpoint endpoint,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = CreateRequest(endpoint, request);
        var (response, body, failure) = await OpenAsync(httpRequest, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        using var _ = response;
        var parser = new AnthropicStreamParser(endpoint.ModelId);
        await using var messages = SseReader.ReadAsync(body!, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            List<ModelEvent> step;
            try
            {
                if (!await messages.MoveNextAsync().ConfigureAwait(false))
                {
                    break;
                }

                step = parser.Handle(messages.Current);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The HttpClient timeout fired while the body was being read.
                step = parser.Abort("timeout", "The upstream did not finish the response in time.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                step = parser.Abort("stream_dropped", "The stream was cut off before it finished.");
            }

            foreach (var modelEvent in step)
            {
                yield return modelEvent;
            }

            if (parser.IsTerminal)
            {
                yield break;
            }
        }

        foreach (var modelEvent in parser.EndOfStream())
        {
            yield return modelEvent;
        }
    }

    private static HttpRequestMessage CreateRequest(ProviderEndpoint endpoint, ModelRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.BaseUrl);
        var body = AnthropicRequestMapper.Build(request, endpoint.ModelId);
        var message = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            message.Headers.TryAddWithoutValidation("x-api-key", endpoint.ApiKey);
        }

        if (endpoint.ExtraHeaders is not null)
        {
            foreach (var (name, value) in endpoint.ExtraHeaders)
            {
                message.Headers.Remove(name);
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    /// <summary>Sends the request and returns the open body, or the failure that ended the call before any event.</summary>
    private async Task<(HttpResponseMessage? Response, Stream? Body, ModelEvent.Failure? Failure)> OpenAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, new ModelEvent.Failure("timeout", "The upstream did not respond in time."));
        }
        catch (HttpRequestException)
        {
            // The detail can name the host, so only the stable code and a generic message leave this adapter.
            return (null, null, new ModelEvent.Failure("provider_error", "The upstream could not be reached."));
        }

        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                return (null, null, await ReadHttpFailureAsync(response, ct).ConfigureAwait(false));
            }
        }

        return (response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), null);
    }

    private static async Task<ModelEvent.Failure> ReadHttpFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await ReadBodyAsync(response, ct).ConfigureAwait(false);
        var message = AnthropicErrors.ReadMessage(body) ?? $"The upstream returned HTTP {status}.";

        if (status != 429)
        {
            return new ModelEvent.Failure("provider_error", message);
        }

        // Spend-cap 429s carry no retry time (docs/design/05-model-gateway.md, quota table), so no retry-after is reported.
        return AnthropicErrors.IsSpendCap(body)
            ? new ModelEvent.Failure("spend_capped", message)
            : new ModelEvent.Failure("rate_limited", message, RetryAfterSeconds(response.Headers));
    }

    /// <summary>The <c>retry-after</c> header in seconds. An HTTP-date form is not used by Anthropic and yields null.</summary>
    private static int? RetryAfterSeconds(HttpResponseHeaders headers) =>
        headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // The status code still classifies the failure, so an unreadable body is treated as empty.
            return "";
        }
    }
}
