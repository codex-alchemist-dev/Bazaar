using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters.Common;

namespace Bazaar.Server.Gateway.Adapters.Gemini;

/// <summary>
/// Adapter for the Gemini <c>generateContent</c> family (docs/design/05-model-gateway.md). It streams
/// <c>models/{model}:streamGenerateContent?alt=sse</c>; each SSE data line is a whole <c>GenerateContentResponse</c>.
/// The key travels in the <c>x-goog-api-key</c> header, never in the URL. Upstream problems surface as
/// <see cref="ModelEvent.Failure"/> events; only the caller's cancellation throws.
/// </summary>
public sealed class GeminiGenerateContentAdapter : IModelAdapter
{
    private readonly HttpClient _http;

    public GeminiGenerateContentAdapter(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
    }

    public AdapterKind Kind => AdapterKind.GeminiGenerateContent;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ProviderEndpoint endpoint,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(request);

        var body = GeminiRequestBuilder.Build(request).ToJsonString();
        using var httpRequest = BuildRequest(endpoint, body);

        var (response, sendFailure) = await SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (response is null)
        {
            yield return sendFailure ?? new ModelEvent.Failure("provider_error", "The provider returned no response.");
            yield break;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorBodyAsync(response, ct).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                yield return ClassifyError(status, (error as JsonObject)?["error"] as JsonObject, RetryAfterSeconds(response.Headers));
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var messages = SseReader.ReadAsync(stream, ct).GetAsyncEnumerator(ct);
            var state = new StreamState();
            ModelEvent.Failure? failure = null;

            while (true)
            {
                SseMessage message;
                try
                {
                    if (!await messages.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    message = messages.Current;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    failure = new ModelEvent.Failure("timeout", "The provider did not respond in time.");
                    break;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    failure = StreamDropped();
                    break;
                }

                if (!TryParseChunk(message.Data, out var chunk))
                {
                    failure = new ModelEvent.Failure("provider_error", "The stream sent a chunk that is not a JSON object.");
                    break;
                }

                foreach (var evt in state.Apply(chunk))
                {
                    yield return evt;
                }

                if (state.Stop)
                {
                    break;
                }
            }

            failure ??= state.Failure;
            if (failure is null && !state.Blocked && state.FinishReason is null)
            {
                failure = StreamDropped();
            }

            if (state.Usage is { } usage)
            {
                yield return new ModelEvent.UsageReported(usage);
            }

            if (failure is not null)
            {
                yield return failure;
                yield break;
            }

            yield return new ModelEvent.Done(state.ResolveStopReason(), state.ModelVersion ?? endpoint.ModelId);
        }
    }

    private static HttpRequestMessage BuildRequest(ProviderEndpoint endpoint, string body)
    {
        var uri = $"{endpoint.BaseUrl.TrimEnd('/')}/models/{endpoint.ModelId}:streamGenerateContent?alt=sse";
        var message = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (endpoint.ExtraHeaders is not null)
        {
            foreach (var (name, value) in endpoint.ExtraHeaders)
            {
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (!string.IsNullOrEmpty(endpoint.ApiKey))
        {
            message.Headers.Remove("x-goog-api-key");
            message.Headers.TryAddWithoutValidation("x-goog-api-key", endpoint.ApiKey);
        }

        return message;
    }

    private async Task<(HttpResponseMessage? Response, ModelEvent.Failure? Failure)> SendAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try
        {
            var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (response, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, new ModelEvent.Failure("timeout", "The provider did not respond in time."));
        }
        catch (HttpRequestException)
        {
            return (null, new ModelEvent.Failure("provider_error", "The provider could not be reached."));
        }
    }

    private static async Task<JsonNode?> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool TryParseChunk(string data, [NotNullWhen(true)] out JsonObject? chunk)
    {
        try
        {
            chunk = JsonNode.Parse(data) as JsonObject;
            return chunk is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            chunk = null;
            return false;
        }
    }

    private static ModelEvent.Failure StreamDropped() =>
        new("stream_dropped", "The stream ended before the response finished.");

    /// <summary>Maps an error object (HTTP body or in-stream) to a failure. 429 is a rate limit unless the text names billing or spend.</summary>
    private static ModelEvent.Failure ClassifyError(int status, JsonObject? error, int? retryAfterSeconds)
    {
        var message = Str(error, "message") ?? $"The provider returned HTTP {status}.";
        if (status != 429)
        {
            return new ModelEvent.Failure("provider_error", message);
        }

        if (message.Contains("billing", StringComparison.OrdinalIgnoreCase) || message.Contains("spend", StringComparison.OrdinalIgnoreCase))
        {
            return new ModelEvent.Failure("spend_capped", message);
        }

        return new ModelEvent.Failure("rate_limited", message, retryAfterSeconds ?? RetryInfoSeconds(error));
    }

    /// <summary>The <c>retryDelay</c> of a <c>google.rpc.RetryInfo</c> detail, such as <c>"32s"</c>, rounded up to whole seconds.</summary>
    private static int? RetryInfoSeconds(JsonObject? error)
    {
        if (error?["details"] is not JsonArray details)
        {
            return null;
        }

        foreach (var detail in details)
        {
            if (detail is JsonObject d && Str(d, "retryDelay") is { } delay
                && double.TryParse(delay.TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds >= 0)
            {
                return (int)Math.Ceiling(seconds);
            }
        }

        return null;
    }

    /// <summary>The Retry-After header, as delta seconds or an HTTP date.</summary>
    private static int? RetryAfterSeconds(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Retry-After", out var values) || values.FirstOrDefault() is not { } raw)
        {
            return null;
        }

        raw = raw.Trim();
        if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return Math.Max(0, seconds);
        }

        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            return Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        return null;
    }

    private static string? Str(JsonObject? obj, string name) =>
        obj?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Bool(JsonObject? obj, string name) =>
        obj?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    /// <summary>A token count. Accepts a JSON number or a numeric string (int64 fields are strings in protobuf JSON).</summary>
    private static long? Int64(JsonObject? obj, string name)
    {
        if (obj?[name] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Folds the chunks of one streamed response into events. Holds the tool-call counter, the last finish reason,
    /// the last usage seen for each field, and the last model version.
    /// </summary>
    private sealed class StreamState
    {
        private long? _prompt;
        private long? _cached;
        private long? _candidates;
        private long? _thoughts;
        private int _toolCalls;

        public string? FinishReason { get; private set; }

        public string? ModelVersion { get; private set; }

        public bool Blocked { get; private set; }

        /// <summary>Set when the chunk carried an error object; the stream stops there.</summary>
        public ModelEvent.Failure? Failure { get; private set; }

        /// <summary>True once the response is over (blocked, or failed in-stream).</summary>
        public bool Stop => Blocked || Failure is not null;

        public GatewayTokenUsage? Usage
        {
            get
            {
                if (_prompt is null && _cached is null && _candidates is null && _thoughts is null)
                {
                    return null;
                }

                var cached = _cached ?? 0;
                var thoughts = _thoughts ?? 0;
                return new GatewayTokenUsage(
                    Input: Math.Max(0, (_prompt ?? 0) - cached),
                    CacheWrite5m: 0,
                    CacheWrite1h: 0,
                    CacheRead: cached,
                    Output: (_candidates ?? 0) + thoughts,
                    Thinking: thoughts);
            }
        }

        public StopReason ResolveStopReason()
        {
            if (Blocked)
            {
                return StopReason.ContentFilter;
            }

            return FinishReason switch
            {
                "STOP" => _toolCalls > 0 ? StopReason.ToolUse : StopReason.EndTurn,
                "MAX_TOKENS" => StopReason.MaxTokens,
                "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" => StopReason.ContentFilter,
                _ => StopReason.Other,
            };
        }

        public List<ModelEvent> Apply(JsonObject chunk)
        {
            var events = new List<ModelEvent>();

            if (chunk["error"] is JsonObject error)
            {
                Failure = ClassifyError(Int64(error, "code") is { } code ? (int)code : 0, error, null);
                return events;
            }

            if (Str(chunk, "modelVersion") is { Length: > 0 } version)
            {
                ModelVersion = version;
            }

            if (chunk["usageMetadata"] is JsonObject usage)
            {
                _prompt = Int64(usage, "promptTokenCount") ?? _prompt;
                _cached = Int64(usage, "cachedContentTokenCount") ?? _cached;
                _candidates = Int64(usage, "candidatesTokenCount") ?? _candidates;
                _thoughts = Int64(usage, "thoughtsTokenCount") ?? _thoughts;
            }

            var hasCandidates = chunk["candidates"] is JsonArray { Count: > 0 };
            if (chunk["promptFeedback"] is JsonObject feedback && Str(feedback, "blockReason") is { } reason && !hasCandidates)
            {
                Blocked = true;
                events.Add(new ModelEvent.Warning("prompt_blocked", $"The provider blocked the prompt ({reason})."));
                return events;
            }

            if (chunk["candidates"] is JsonArray { Count: > 0 } candidates
                && candidates[0] is JsonObject candidate)
            {
                if (Str(candidate, "finishReason") is { Length: > 0 } finish)
                {
                    FinishReason = finish;
                }

                if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts)
                {
                    foreach (var part in parts)
                    {
                        if (part is JsonObject p)
                        {
                            ApplyPart(p, events);
                        }
                    }
                }
            }

            return events;
        }

        private void ApplyPart(JsonObject part, List<ModelEvent> events)
        {
            if (part["functionCall"] is JsonObject call)
            {
                // Gemini gives no call id, so ids are synthesized per response in arrival order.
                var id = $"call_{_toolCalls++}";
                events.Add(new ModelEvent.ToolCallStart(id, Str(call, "name") ?? "unknown"));
                events.Add(new ModelEvent.ToolCallArgsDelta(id, call["args"]?.ToJsonString() ?? "{}"));
                events.Add(new ModelEvent.ToolCallEnd(id));
                return;
            }

            if (Str(part, "text") is { Length: > 0 } text)
            {
                events.Add(Bool(part, "thought") ? new ModelEvent.ThinkingDelta(text) : new ModelEvent.TextDelta(text));
            }
        }
    }
}
