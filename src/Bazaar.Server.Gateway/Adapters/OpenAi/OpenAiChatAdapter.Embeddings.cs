using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Gateway.Adapters.OpenAi;

/// <summary>
/// Embeddings over the OpenAI-compatible <c>POST {base}/embeddings</c> endpoint, which FreeLLMAPI, OmniRoute, reverse
/// proxies and local runtimes all speak. HTTP errors are classified exactly as for chat.
/// </summary>
public sealed partial class OpenAiChatAdapter
{
    public async Task<EmbeddingOutcome> EmbedAsync(ProviderEndpoint endpoint, EmbeddingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        using var httpRequest = BuildEmbeddingRequest(endpoint, request);
        var sent = await SendAsync(httpRequest, ct);
        using var response = sent.Response!;
        if (sent.Error is ModelEvent.Failure sendError)
        {
            return new EmbeddingOutcome.Failed(sendError);
        }

        endpoint.OnResponseHeaders?.Invoke(response.Headers);
        if (!response.IsSuccessStatusCode)
        {
            return new EmbeddingOutcome.Failed((ModelEvent.Failure)await FailureFromStatusAsync(response, ct));
        }

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new EmbeddingOutcome.Failed(TimeoutFailure());
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return new EmbeddingOutcome.Failed(StreamDroppedFailure());
        }

        return ParseEmbeddingResponse(body, request.Inputs.Count);
    }

    private static HttpRequestMessage BuildEmbeddingRequest(ProviderEndpoint endpoint, EmbeddingRequest request)
    {
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        string json;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("model", endpoint.ModelId);
                writer.WriteStartArray("input");
                foreach (var input in request.Inputs)
                {
                    writer.WriteStringValue(input);
                }

                writer.WriteEndArray();
                writer.WriteString("encoding_format", "float");
                if (request.Dimensions is { } dimensions)
                {
                    writer.WriteNumber("dimensions", dimensions);
                }

                writer.WriteEndObject();
            }

            json = Encoding.UTF8.GetString(stream.ToArray());
        }

        var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}/embeddings", UriKind.Absolute))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        }

        if (endpoint.ExtraHeaders is not null)
        {
            foreach (var header in endpoint.ExtraHeaders)
            {
                message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return message;
    }

    private static EmbeddingOutcome ParseEmbeddingResponse(string body, int expectedCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Malformed();
            }

            // Providers return items in input order, each tagged with its index; honour the index when present.
            var vectors = new float[expectedCount][];
            var filled = 0;
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var i) ? i : position;
                position++;
                if (index < 0 || index >= expectedCount || vectors[index] is not null
                    || !item.TryGetProperty("embedding", out var embedding) || embedding.ValueKind != JsonValueKind.Array)
                {
                    return Malformed();
                }

                var vector = new float[embedding.GetArrayLength()];
                var n = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    vector[n++] = value.GetSingle();
                }

                vectors[index] = vector;
                filled++;
            }

            if (filled != expectedCount)
            {
                return Malformed();
            }

            var served = root.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null;
            long tokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt64(out var p))
                {
                    tokens = p;
                }
                else if (usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt64(out var t))
                {
                    tokens = t;
                }
            }

            return new EmbeddingOutcome.Succeeded(vectors, string.IsNullOrEmpty(served) ? null : served, tokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Malformed();
        }
    }

    private static EmbeddingOutcome.Failed Malformed() =>
        new(new ModelEvent.Failure("provider_error", "The upstream sent an embeddings response that does not match the request."));
}
