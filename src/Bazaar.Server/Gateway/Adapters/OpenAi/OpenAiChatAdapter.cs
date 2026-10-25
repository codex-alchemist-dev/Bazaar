using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters.Common;

namespace Bazaar.Server.Gateway.Adapters.OpenAi;

/// <summary>
/// Streams one call to an OpenAI-compatible <c>/chat/completions</c> endpoint and maps it to <see cref="ModelEvent"/>.
/// Upstream problems (HTTP errors, rate limits, timeouts, dropped or malformed streams) end the stream with a
/// <see cref="ModelEvent.Failure"/>; only the caller's own cancellation propagates as an exception.
/// </summary>
public sealed class OpenAiChatAdapter : IModelAdapter
{
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public OpenAiChatAdapter(HttpClient http, TimeProvider? time = null)
    {
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public AdapterKind Kind => AdapterKind.OpenAiChat;

    public async IAsyncEnumerable<ModelEvent> StreamAsync(
        ProviderEndpoint endpoint,
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var httpRequest = BuildHttpRequest(endpoint, request);
        var sent = await SendAsync(httpRequest, ct);
        using var response = sent.Response!;
        if (sent.Error is { } sendError)
        {
            yield return sendError;
            yield break;
        }

        if (!response.IsSuccessStatusCode)
        {
            yield return await FailureFromStatusAsync(response, ct);
            yield break;
        }

        await using var events = ReadEventsAsync(response, endpoint.ModelId, ct).GetAsyncEnumerator(ct);
        while (true)
        {
            // The body read is the only place a timeout or a dropped connection shows up, so it is wrapped here.
            // Yields stay outside the try blocks.
            ModelEvent? readError = null;
            var hasNext = false;
            try
            {
                hasNext = await events.MoveNextAsync();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                readError = TimeoutFailure();
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                readError = StreamDroppedFailure();
            }

            if (readError is not null)
            {
                yield return readError;
                yield break;
            }

            if (!hasNext)
            {
                yield break;
            }

            var next = events.Current;
            yield return next;
            if (next is ModelEvent.Done or ModelEvent.Failure)
            {
                yield break;
            }
        }
    }

    private static HttpRequestMessage BuildHttpRequest(ProviderEndpoint endpoint, ModelRequest request)
    {
        var baseUrl = endpoint.BaseUrl.TrimEnd('/');
        var message = new HttpRequestMessage(HttpMethod.Post, new Uri($"{baseUrl}/chat/completions", UriKind.Absolute))
        {
            Content = new StringContent(OpenAiChatRequestBuilder.Build(endpoint.ModelId, request), Encoding.UTF8, "application/json"),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
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

    private async Task<(HttpResponseMessage? Response, ModelEvent? Error)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return (response, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, TimeoutFailure());
        }
        catch (HttpRequestException)
        {
            return (null, new ModelEvent.Failure("provider_error", "The upstream could not be reached."));
        }
    }

    private async Task<ModelEvent> FailureFromStatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadBodyOrEmptyAsync(response, ct);
        var (code, message) = ReadErrorBody(body);
        var status = (int)response.StatusCode;
        if (status == 429)
        {
            return IsSpendCap(code, message)
                ? new ModelEvent.Failure("spend_capped", "The upstream spend cap is reached.")
                : new ModelEvent.Failure("rate_limited", "The upstream rate limit is reached.", RetryAfterSeconds(response.Headers.RetryAfter));
        }

        var suffix = message is null ? "" : $" {Truncate(message, 300)}";
        return new ModelEvent.Failure("provider_error", $"The upstream returned HTTP {status}.{suffix}");
    }

    private static async Task<string> ReadBodyOrEmptyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "";
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            return "";
        }
    }

    private int? RetryAfterSeconds(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter?.Delta is { } delta)
        {
            return Seconds(delta.TotalSeconds);
        }

        if (retryAfter?.Date is { } date)
        {
            return Seconds((date - _time.GetUtcNow()).TotalSeconds);
        }

        return null;
    }

    /// <summary>Reads the body lazily, so events reach the caller as the upstream streams them.</summary>
    private static async IAsyncEnumerable<ModelEvent> ReadEventsAsync(
        HttpResponseMessage response,
        string requestedModel,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var calls = new Dictionary<int, ToolCallState>();
        var sawDone = false;
        StopReason? stopReason = null;
        string? servedModel = null;

        await foreach (var message in SseReader.ReadAsync(body, ct))
        {
            if (message.Data == "[DONE]")
            {
                sawDone = true;
                break;
            }

            if (!TryParseChunk(message.Data, out var chunk))
            {
                yield return new ModelEvent.Failure("provider_error", "The stream sent a chunk that is not valid JSON.");
                yield break;
            }

            if (chunk is null)
            {
                continue;
            }

            if (chunk.Error is JsonElement error && error.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                var (code, text) = ReadErrorElement(error);
                yield return IsSpendCap(code, text)
                    ? new ModelEvent.Failure("spend_capped", "The upstream spend cap is reached.")
                    : new ModelEvent.Failure("provider_error", text is null ? "The stream reported an error." : Truncate(text, 300));
                yield break;
            }

            if (chunk.Model is { Length: > 0 } model)
            {
                servedModel = model;
            }

            if (chunk.Usage is { } usage)
            {
                yield return new ModelEvent.UsageReported(MapUsage(usage));
            }

            var choice = chunk.Choices is { Count: > 0 } choices ? choices[0] : null;
            if (choice?.Delta is { } delta)
            {
                var reasoning = FirstNonEmpty(delta.ReasoningContent, delta.Reasoning);
                if (reasoning is not null)
                {
                    yield return new ModelEvent.ThinkingDelta(reasoning);
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    yield return new ModelEvent.TextDelta(delta.Content);
                }

                if (delta.ToolCalls is { Count: > 0 } toolCalls)
                {
                    var pending = new List<ModelEvent>();
                    AssembleToolCalls(calls, toolCalls, pending);
                    foreach (var e in pending)
                    {
                        yield return e;
                    }
                }
            }

            if (choice?.FinishReason is { Length: > 0 } finish && stopReason is null)
            {
                stopReason = MapFinishReason(finish);
                foreach (var e in CloseToolCalls(calls))
                {
                    yield return e;
                    if (e is ModelEvent.Failure)
                    {
                        yield break;
                    }
                }
            }
        }

        // A stream with neither a finish reason nor [DONE] was cut off.
        if (!sawDone && stopReason is null)
        {
            yield return StreamDroppedFailure();
            yield break;
        }

        foreach (var e in CloseToolCalls(calls))
        {
            yield return e;
            if (e is ModelEvent.Failure)
            {
                yield break;
            }
        }

        yield return new ModelEvent.Done(stopReason ?? StopReason.Other, servedModel ?? requestedModel);
    }

    /// <summary>
    /// Builds the tool calls from their fragments. A call starts once it has both its id and its name;
    /// argument fragments that arrive before that are held until the start event.
    /// </summary>
    private static void AssembleToolCalls(Dictionary<int, ToolCallState> calls, List<OpenAiChunkToolCall> deltas, List<ModelEvent> events)
    {
        foreach (var delta in deltas)
        {
            if (!calls.TryGetValue(delta.Index, out var state))
            {
                state = new ToolCallState();
                calls[delta.Index] = state;
            }

            state.Id ??= NullIfEmpty(delta.Id);
            state.Name ??= NullIfEmpty(delta.Function?.Name);
            if (!state.Started && state.Id is not null && state.Name is not null)
            {
                StartCall(state, events);
            }

            var fragment = delta.Function?.Arguments;
            if (string.IsNullOrEmpty(fragment))
            {
                continue;
            }

            state.Arguments.Append(fragment);
            if (state.Started)
            {
                events.Add(new ModelEvent.ToolCallArgsDelta(state.Id!, fragment));
            }
            else
            {
                state.Unsent.Append(fragment);
            }
        }
    }

    /// <summary>
    /// Ends every open tool call in index order. A call without a name is a provider error. A call whose arguments
    /// are not a JSON object ends with <c>malformed_tool_json</c>, and nothing after it is emitted.
    /// </summary>
    private static List<ModelEvent> CloseToolCalls(Dictionary<int, ToolCallState> calls)
    {
        var events = new List<ModelEvent>();
        foreach (var index in calls.Keys.Order())
        {
            var state = calls[index];
            if (state.Ended)
            {
                continue;
            }

            if (!state.Started)
            {
                if (state.Name is null)
                {
                    events.Add(new ModelEvent.Failure("provider_error", "The stream sent a tool call without a name."));
                    return events;
                }

                state.Id ??= $"call_{index}";
                StartCall(state, events);
            }

            state.Ended = true;
            events.Add(new ModelEvent.ToolCallEnd(state.Id!));
            if (!IsJsonObjectOrEmpty(state.Arguments.ToString()))
            {
                events.Add(new ModelEvent.Failure("malformed_tool_json", $"Tool call '{state.Id}' has arguments that are not a JSON object."));
                return events;
            }
        }

        return events;
    }

    private static void StartCall(ToolCallState state, List<ModelEvent> events)
    {
        state.Started = true;
        events.Add(new ModelEvent.ToolCallStart(state.Id!, state.Name!));
        if (state.Unsent.Length > 0)
        {
            events.Add(new ModelEvent.ToolCallArgsDelta(state.Id!, state.Unsent.ToString()));
            state.Unsent.Clear();
        }
    }

    /// <summary>Empty arguments count as no arguments; anything else must parse to a JSON object.</summary>
    private static bool IsJsonObjectOrEmpty(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GatewayTokenUsage MapUsage(OpenAiChunkUsage usage)
    {
        var prompt = usage.PromptTokens ?? 0;
        var cached = usage.PromptTokensDetails?.CachedTokens ?? 0;
        return new GatewayTokenUsage(
            Input: Math.Max(0, prompt - cached),
            CacheWrite5m: 0,
            CacheWrite1h: 0,
            CacheRead: cached,
            Output: usage.CompletionTokens ?? 0,
            Thinking: usage.CompletionTokensDetails?.ReasoningTokens ?? 0);
    }

    private static StopReason MapFinishReason(string reason) => reason switch
    {
        "stop" => StopReason.EndTurn,
        "tool_calls" or "function_call" => StopReason.ToolUse,
        "length" => StopReason.MaxTokens,
        "content_filter" => StopReason.ContentFilter,
        _ => StopReason.Other,
    };

    /// <summary>A 429 is a spend cap, not a rate limit, when the body names a quota or spend-limit code.</summary>
    private static bool IsSpendCap(string? code, string? message)
    {
        if (code is "insufficient_quota" || code?.Contains("spend", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return message is not null && (
            message.Contains("spend limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("spending limit", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase));
    }

    private static (string? Code, string? Message) ReadErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                return ReadErrorElement(error);
            }
        }
        catch (JsonException)
        {
            // Not JSON: the status code alone describes the failure.
        }

        return (null, null);
    }

    /// <summary>Reads an error object (<c>code</c> or <c>type</c>, and <c>message</c>), or a bare error string.</summary>
    private static (string? Code, string? Message) ReadErrorElement(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return (null, error.GetString());
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        return (ReadField(error, "code") ?? ReadField(error, "type"), ReadField(error, "message"));
    }

    private static string? ReadField(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var field))
        {
            return null;
        }

        return field.ValueKind switch
        {
            JsonValueKind.String => field.GetString(),
            JsonValueKind.Number => field.GetRawText(),
            _ => null,
        };
    }

    private static bool TryParseChunk(string json, out OpenAiChatChunk? chunk)
    {
        try
        {
            chunk = JsonSerializer.Deserialize(json, OpenAiChatJsonContext.Default.OpenAiChatChunk);
            return true;
        }
        catch (JsonException)
        {
            chunk = null;
            return false;
        }
    }

    private static ModelEvent.Failure TimeoutFailure() => new("timeout", "The upstream did not respond in time.");

    private static ModelEvent.Failure StreamDroppedFailure() => new("stream_dropped", "The stream was cut off before it finished.");

    private static int Seconds(double seconds) => (int)Math.Clamp(Math.Ceiling(seconds), 0d, int.MaxValue);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? FirstNonEmpty(string? first, string? second) =>
        !string.IsNullOrEmpty(first) ? first : NullIfEmpty(second);

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>One tool call being assembled from its index-keyed fragments.</summary>
    private sealed class ToolCallState
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public bool Started { get; set; }

        public bool Ended { get; set; }

        /// <summary>Every argument fragment, kept to validate the JSON at the end.</summary>
        public StringBuilder Arguments { get; } = new();

        /// <summary>Fragments received before the call started; emitted at the start event.</summary>
        public StringBuilder Unsent { get; } = new();
    }
}
