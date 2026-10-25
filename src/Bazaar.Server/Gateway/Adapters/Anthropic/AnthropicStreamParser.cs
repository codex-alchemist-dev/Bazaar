using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters.Common;

namespace Bazaar.Server.Gateway.Adapters.Anthropic;

/// <summary>
/// Turns Anthropic stream events into <see cref="ModelEvent"/>s. Holds the state of one call: open tool-use blocks,
/// their argument buffers, and the usage merged from <c>message_start</c> and <c>message_delta</c>.
/// Usage is emitted once, just before the terminal event, so it carries the final cumulative counts.
/// </summary>
internal sealed class AnthropicStreamParser
{
    private readonly string _requestedModel;
    private readonly Dictionary<int, ToolBlock> _tools = [];
    private StopReason _stopReason = StopReason.Other;
    private string? _servedModel;
    private bool _sawUsage;
    private long? _input;
    private long? _cacheRead;
    private long? _output;
    private long? _cacheWriteFlat;
    private long? _cacheWrite5m;
    private long? _cacheWrite1h;

    public AnthropicStreamParser(string requestedModel) => _requestedModel = requestedModel;

    /// <summary>True once the call has ended with <c>Done</c> or <c>Failure</c>. Nothing else is read after that.</summary>
    public bool IsTerminal { get; private set; }

    /// <summary>Translates one SSE event. Ping and unknown events produce nothing.</summary>
    public List<ModelEvent> Handle(SseMessage message)
    {
        var events = new List<ModelEvent>();
        if (IsTerminal)
        {
            return events;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(message.Data);
        }
        catch (JsonException)
        {
            Fail(events, "provider_error", "The upstream sent an event that is not valid JSON.");
            return events;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                Fail(events, "provider_error", "The upstream sent an event that is not a JSON object.");
                return events;
            }

            switch (message.Event ?? StringProp(root, "type"))
            {
                case "message_start":
                    OnMessageStart(root);
                    break;

                case "content_block_start":
                    OnContentBlockStart(root, events);
                    break;

                case "content_block_delta":
                    OnContentBlockDelta(root, events);
                    break;

                case "content_block_stop":
                    OnContentBlockStop(root, events);
                    break;

                case "message_delta":
                    OnMessageDelta(root);
                    break;

                case "message_stop":
                    Complete(events);
                    break;

                case "error":
                    OnError(root, events);
                    break;
            }
        }

        return events;
    }

    /// <summary>Ends the call with a failure, such as a timeout or a dropped connection. Does nothing if the call already ended.</summary>
    public List<ModelEvent> Abort(string code, string message, int? retryAfterSeconds = null)
    {
        var events = new List<ModelEvent>();
        Fail(events, code, message, retryAfterSeconds);
        return events;
    }

    /// <summary>Called when the stream ends. A stream that ends before <c>message_stop</c> is dropped.</summary>
    public List<ModelEvent> EndOfStream() =>
        IsTerminal ? [] : Abort("stream_dropped", "The stream ended before the response finished.");

    private void OnMessageStart(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
        {
            _servedModel = StringProp(message, "model") ?? _servedModel;
            if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                MergeUsage(usage);
            }
        }
    }

    private void OnContentBlockStart(JsonElement root, List<ModelEvent> events)
    {
        var index = IndexOf(root);
        if (!root.TryGetProperty("content_block", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (StringProp(block, "type"))
        {
            case "text":
                if (StringProp(block, "text") is { Length: > 0 } text)
                {
                    events.Add(new ModelEvent.TextDelta(text));
                }

                break;

            case "thinking":
                if (StringProp(block, "thinking") is { Length: > 0 } thinking)
                {
                    events.Add(new ModelEvent.ThinkingDelta(thinking));
                }

                break;

            case "tool_use":
                var id = StringProp(block, "id");
                var name = StringProp(block, "name");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                {
                    Fail(events, "provider_error", "The upstream started a tool call without an id or a name.");
                    return;
                }

                _tools[index] = new ToolBlock(id, new StringBuilder());
                events.Add(new ModelEvent.ToolCallStart(id, name));
                break;

            // redacted_thinking and unknown block types carry no visible output here, so they are ignored.
        }
    }

    private void OnContentBlockDelta(JsonElement root, List<ModelEvent> events)
    {
        var index = IndexOf(root);
        if (!root.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        switch (StringProp(delta, "type"))
        {
            case "text_delta":
                if (StringProp(delta, "text") is { Length: > 0 } text)
                {
                    events.Add(new ModelEvent.TextDelta(text));
                }

                break;

            case "thinking_delta":
                if (StringProp(delta, "thinking") is { Length: > 0 } thinking)
                {
                    events.Add(new ModelEvent.ThinkingDelta(thinking));
                }

                break;

            case "input_json_delta":
                if (_tools.TryGetValue(index, out var tool) && StringProp(delta, "partial_json") is { Length: > 0 } fragment)
                {
                    tool.Arguments.Append(fragment);
                    events.Add(new ModelEvent.ToolCallArgsDelta(tool.CallId, fragment));
                }

                break;

            // signature_delta is ignored: it is a verification token for replaying thinking, not output.
        }
    }

    private void OnContentBlockStop(JsonElement root, List<ModelEvent> events)
    {
        if (!_tools.Remove(IndexOf(root), out var tool))
        {
            return;
        }

        var arguments = tool.Arguments.Length == 0 ? "{}" : tool.Arguments.ToString();
        if (!IsJsonObject(arguments))
        {
            Fail(events, "malformed_tool_json", $"Tool call '{tool.CallId}' has arguments that are not a valid JSON object.");
            return;
        }

        events.Add(new ModelEvent.ToolCallEnd(tool.CallId));
    }

    private void OnMessageDelta(JsonElement root)
    {
        if (root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
            && StringProp(delta, "stop_reason") is { } reason)
        {
            _stopReason = MapStopReason(reason);
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            MergeUsage(usage);
        }
    }

    private void OnError(JsonElement root, List<ModelEvent> events)
    {
        var error = root.TryGetProperty("error", out var element) ? element : default;
        var type = StringProp(error, "type");
        var message = StringProp(error, "message") ?? "The upstream reported an error.";

        if (type == "rate_limit_error")
        {
            Fail(events, AnthropicErrors.IsSpendCap(message) ? "spend_capped" : "rate_limited", message);
            return;
        }

        // overloaded_error and any other in-stream error are provider faults.
        Fail(events, "provider_error", message);
    }

    private void Complete(List<ModelEvent> events)
    {
        EmitUsage(events);
        events.Add(new ModelEvent.Done(_stopReason, _servedModel ?? _requestedModel));
        IsTerminal = true;
    }

    private void Fail(List<ModelEvent> events, string code, string message, int? retryAfterSeconds = null)
    {
        if (IsTerminal)
        {
            return;
        }

        EmitUsage(events);
        events.Add(new ModelEvent.Failure(code, message, retryAfterSeconds));
        IsTerminal = true;
    }

    private void EmitUsage(List<ModelEvent> events)
    {
        if (_sawUsage)
        {
            events.Add(new ModelEvent.UsageReported(Usage()));
        }
    }

    /// <summary>
    /// Merges one usage object. <c>message_delta</c> usage is cumulative, so a field present there replaces the earlier value
    /// and an absent field keeps it.
    /// </summary>
    private void MergeUsage(JsonElement usage)
    {
        _sawUsage = true;
        _input = LongProp(usage, "input_tokens") ?? _input;
        _cacheRead = LongProp(usage, "cache_read_input_tokens") ?? _cacheRead;
        _output = LongProp(usage, "output_tokens") ?? _output;
        _cacheWriteFlat = LongProp(usage, "cache_creation_input_tokens") ?? _cacheWriteFlat;

        if (usage.TryGetProperty("cache_creation", out var breakdown) && breakdown.ValueKind == JsonValueKind.Object)
        {
            _cacheWrite5m = LongProp(breakdown, "ephemeral_5m_input_tokens") ?? _cacheWrite5m;
            _cacheWrite1h = LongProp(breakdown, "ephemeral_1h_input_tokens") ?? _cacheWrite1h;
        }
    }

    /// <summary>
    /// The 5-minute and 1-hour cache writes stay separate because they are priced differently (decision C33).
    /// The flat <c>cache_creation_input_tokens</c> is used only when no breakdown was sent, and then counts as 5-minute.
    /// Thinking is 0: Anthropic does not report it separately, and it is already inside output tokens.
    /// </summary>
    private GatewayTokenUsage Usage()
    {
        var hasBreakdown = _cacheWrite5m is not null || _cacheWrite1h is not null;
        var write5m = hasBreakdown ? (_cacheWrite5m ?? 0) : (_cacheWriteFlat ?? 0);
        var write1h = hasBreakdown ? (_cacheWrite1h ?? 0) : 0;
        return new GatewayTokenUsage(_input ?? 0, write5m, write1h, _cacheRead ?? 0, _output ?? 0, 0);
    }

    private static StopReason MapStopReason(string reason) => reason switch
    {
        "end_turn" or "stop_sequence" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "refusal" => StopReason.ContentFilter,
        _ => StopReason.Other,
    };

    private static int IndexOf(JsonElement root) => LongProp(root, "index") is { } index ? (int)index : -1;

    private static string? StringProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? LongProp(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : null;

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed class ToolBlock(string callId, StringBuilder arguments)
    {
        public string CallId { get; } = callId;

        public StringBuilder Arguments { get; } = arguments;
    }
}
