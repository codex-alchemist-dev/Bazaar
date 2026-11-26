using System.Net.Http.Headers;
using System.Text.Json;
using Bazaar.Server.Secrets;

namespace Bazaar.Server.Embeddings;

/// <summary>
/// Embeddings over the OpenAI-compatible HTTP surface: <c>POST {base}/v1/embeddings</c> with <c>{model, input[]}</c>. Covers local
/// runtimes (Ollama, llama.cpp, LM Studio) and API proxies. Texts go out in batches of <c>BatchSize</c>; the reply is ordered by
/// each item's <c>index</c> and checked for count and dimension.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly int _batchSize;
    private readonly string? _apiKeyReference;
    private readonly ISecretResolver? _secrets;

    public OpenAiCompatibleEmbeddingProvider(HttpClient http, EmbeddingProviderOptions options, ISecretResolver? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.BaseUrl) || !Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Provider '{options.Id}' needs an absolute BaseUrl.", nameof(options));
        }

        if (options.BatchSize < 1)
        {
            throw new ArgumentException($"Provider '{options.Id}' needs a BatchSize of at least 1.", nameof(options));
        }

        _http = http;
        _batchSize = options.BatchSize;
        _apiKeyReference = string.IsNullOrWhiteSpace(options.ApiKeyReference) ? null : options.ApiKeyReference;
        _secrets = secrets;
        Descriptor = new EmbeddingProviderDescriptor(EmbeddingProviderKind.OpenAiCompatible, options.ModelId, options.Dimensions);

        // A base that already ends in /v1 (common for proxies) must not get a second one.
        var root = baseUri.AbsoluteUri.TrimEnd('/');
        _endpoint = new Uri(root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? root + "/embeddings" : root + "/v1/embeddings");
    }

    public EmbeddingProviderDescriptor Descriptor { get; }

    public async Task<EmbeddingResult> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var vectors = new List<float[]>(texts.Count);
        for (var offset = 0; offset < texts.Count; offset += _batchSize)
        {
            var batch = texts.Skip(offset).Take(_batchSize).ToArray();
            vectors.AddRange(await EmbedBatchAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        return new EmbeddingResult(vectors);
    }

    private async Task<IReadOnlyList<float[]>> EmbedBatchAsync(string[] batch, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = BuildBody(batch) };
        if (_apiKeyReference is not null)
        {
            if (_secrets is null || !_secrets.CanResolve(_apiKeyReference))
            {
                throw new EmbeddingException("The embedding API key reference cannot be resolved.");
            }

            var key = await _secrets.ResolveAsync(_apiKeyReference, cancellationToken).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingException($"The embedding endpoint could not be reached: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingException("The embedding endpoint timed out.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The body is not echoed: it may repeat the input text.
                throw new EmbeddingException($"The embedding endpoint answered HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Parse(json, batch.Length);
        }
    }

    private StringContent BuildBody(string[] batch)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", Descriptor.ModelId);
            writer.WriteStartArray("input");
            foreach (var text in batch)
            {
                writer.WriteStringValue(text);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new StringContent(System.Text.Encoding.UTF8.GetString(stream.ToArray()), System.Text.Encoding.UTF8, "application/json");
    }

    private float[][] Parse(string json, int expectedCount)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            if (data.GetArrayLength() != expectedCount)
            {
                throw new EmbeddingException($"The endpoint returned {data.GetArrayLength()} embeddings for {expectedCount} inputs.");
            }

            var result = new float[expectedCount][];
            var position = 0;
            foreach (var item in data.EnumerateArray())
            {
                var index = item.TryGetProperty("index", out var indexElement) ? indexElement.GetInt32() : position;
                if (index < 0 || index >= expectedCount || result[index] is not null)
                {
                    throw new EmbeddingException("The endpoint returned an invalid or repeated embedding index.");
                }

                var values = item.GetProperty("embedding");
                if (values.GetArrayLength() != Descriptor.Dimensions)
                {
                    throw new EmbeddingException(
                        $"The endpoint returned {values.GetArrayLength()} dimensions; the profile expects {Descriptor.Dimensions}.");
                }

                var vector = new float[Descriptor.Dimensions];
                var i = 0;
                foreach (var value in values.EnumerateArray())
                {
                    vector[i++] = value.GetSingle();
                }

                result[index] = vector;
                position++;
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new EmbeddingException("The embedding endpoint returned a reply that is not a valid embeddings response.", ex);
        }
    }
}
