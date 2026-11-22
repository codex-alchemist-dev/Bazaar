using System.Text.Json;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>
/// Turns CLI <c>stream-json</c> lines into <see cref="ModelEvent"/>s for one call (docs/design/91 section 7). Tolerant by design:
/// unknown events are counted and ignored. One instance per call; not thread safe.
/// </summary>
internal sealed class StreamParser(IReadOnlySet<string> offeredTools, ClaudeCodeOptions options, TimeProvider time)
{
    private readonly Dictionary<int, Block> _blocks = [];
    private bool _sawStreamEvents;
    private long _input;
    private long _output;
    private long _cacheRead;
    private long _cacheWrite5m;
    private long _cacheWrite1h;
    private string? _stopReason;
    private string? _lastApiRetryError;

    /// <summary>True once a completed assistant message ended with a tool call: the host should stop the process now.</summary>
    public bool Halt { get; private set; }

    /// <summary>True once a <c>result</c> event arrived.</summary>
    public bool ResultSeen { get; private set; }

    public string ServedModel { get; private set; } = "";

    public long Ignored { get; private set; }

    /// <summary>Set when <c>system/init</c> shows a built-in tool, a foreign MCP server, or no bridge (docs/design/91 section 12).</summary>
    public string? InitViolation { get; private set; }

    public string? LastApiRetryError => _lastApiRetryError;

    public ClassifiedFailure? ResultFailure { get; private set; }

    public StopReason StopReasonValue => Map(_stopReason);

    public GatewayTokenUsage Usage => new(_input, _cacheWrite5m, _cacheWrite1h, _cacheRead, _output, 0);

    public IReadOnlyList<ModelEvent> Feed(string line)
    {
        var events = new List<ModelEvent>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Ignored++;
            return events;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeEl) || typeEl.GetString() is not { } type)
            {
                Ignored++;
                return events;
            }

            switch (type)
            {
                case "system":
                    OnSystem(root, events);
                    break;
                case "stream_event":
                    _sawStreamEvents = true;
                    if (root.TryGetProperty("event", out var ev))
                    {
                        OnStreamEvent(ev, events);
                    }

                    break;
                case "assistant":
                    OnAssistant(root, events);
                    break;
                case "result":
                    OnResult(root, events);
                    break;
                case "user":
                case "rate_limit_event":
                    break;
                default:
                    Ignored++;
                    break;
            }
        }

        return events;
    }

    private void OnSystem(JsonElement root, List<ModelEvent> events)
    {
        var sub = Str(root, "subtype");
        if (sub == "init")
        {
            ServedModel = Str(root, "model") ?? ServedModel;
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray())
                {
                    var name = t.GetString() ?? "";
                    if (!name.StartsWith(ClaudeCodeArguments.ToolPrefix, StringComparison.Ordinal))
                    {
                        InitViolation = $"the CLI reports a built-in or foreign tool: {Truncate(name)}";
                    }
                }
            }

            var bridge = false;
            if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in servers.EnumerateArray())
                {
                    if (Str(s, "name") == ClaudeCodeArguments.BridgeServerName)
                    {
                        bridge = true;
                    }
                    else
                    {
                        InitViolation = $"the CLI reports a foreign MCP server: {Truncate(Str(s, "name") ?? "")}";
                    }
                }
            }

            if (!bridge)
            {
                InitViolation ??= "the CLI did not load the Bazaar tool bridge";
            }
        }
        else if (sub == "api_retry")
        {
            _lastApiRetryError = Str(root, "error");
            events.Add(new ModelEvent.Warning("upstream_retry", $"upstream retry: {Truncate(_lastApiRetryError ?? "unknown")}"));
        }
    }

    private void OnStreamEvent(JsonElement ev, List<ModelEvent> events)
    {
        switch (Str(ev, "type"))
        {
            case "message_start":
                if (ev.TryGetProperty("message", out var msg))
                {
                    ServedModel = Str(msg, "model") ?? ServedModel;
                    if (msg.TryGetProperty("usage", out var u))
                    {
                        ReadUsage(u, includeOutput: false);
                    }
                }

                break;
            case "content_block_start":
                BlockStart(ev, events);
                break;
            case "content_block_delta":
                BlockDelta(ev, events);
                break;
            case "content_block_stop":
                if (ev.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) && _blocks.TryGetValue(i, out var b) && b.ToolId is not null)
                {
                    events.Add(new ModelEvent.ToolCallEnd(b.ToolId));
                }

                break;
            case "message_delta":
                if (ev.TryGetProperty("delta", out var d))
                {
                    _stopReason = Str(d, "stop_reason") ?? _stopReason;
                }

                if (ev.TryGetProperty("usage", out var du))
                {
                    ReadUsage(du, includeOutput: true);
                }

                break;
            case "message_stop":
                if (_stopReason == "tool_use")
                {
                    Halt = true;
                }

                break;
            default:
                break;
        }
    }

    private void BlockStart(JsonElement ev, List<ModelEvent> events)
    {
        if (!ev.TryGetProperty("index", out var idx) || !idx.TryGetInt32(out var i) || !ev.TryGetProperty("content_block", out var cb))
        {
            return;
        }

        var kind = Str(cb, "type");
        if (kind == "tool_use")
        {
            var id = Str(cb, "id") ?? $"call_{i}";
            var name = Str(cb, "name") ?? "";
            if (name.StartsWith(ClaudeCodeArguments.ToolPrefix, StringComparison.Ordinal))
            {
                name = name[ClaudeCodeArguments.ToolPrefix.Length..];
            }

            if (!offeredTools.Contains(name))
            {
                events.Add(new ModelEvent.Failure("malformed_tool_json", $"the model called a tool that was not offered: {Truncate(name)}"));
                return;
            }

            _blocks[i] = new Block(kind, id);
            events.Add(new ModelEvent.ToolCallStart(id, name));
        }
        else
        {
            _blocks[i] = new Block(kind ?? "", null);
        }
    }

    private void BlockDelta(JsonElement ev, List<ModelEvent> events)
    {
        if (!ev.TryGetProperty("delta", out var d))
        {
            return;
        }

        switch (Str(d, "type"))
        {
            case "text_delta":
                if (Str(d, "text") is { Length: > 0 } text)
                {
                    events.Add(new ModelEvent.TextDelta(text));
                }

                break;
            case "thinking_delta":
                if (Str(d, "thinking") is { Length: > 0 } th)
                {
                    events.Add(new ModelEvent.ThinkingDelta(th));
                }

                break;
            case "input_json_delta":
                if (ev.TryGetProperty("index", out var idx) && idx.TryGetInt32(out var i) && _blocks.TryGetValue(i, out var b) && b.ToolId is not null
                    && Str(d, "partial_json") is { Length: > 0 } frag)
                {
                    events.Add(new ModelEvent.ToolCallArgsDelta(b.ToolId, frag));
                }

                break;
            default:
                break;
        }
    }

    // Older CLI versions may send whole assistant messages without partial events. Used only when no stream event was seen.
    private void OnAssistant(JsonElement root, List<ModelEvent> events)
    {
        if (_sawStreamEvents || !root.TryGetProperty("message", out var msg))
        {
            return;
        }

        ServedModel = Str(msg, "model") ?? ServedModel;
        _stopReason = Str(msg, "stop_reason") ?? _stopReason;
        if (msg.TryGetProperty("usage", out var u))
        {
            ReadUsage(u, includeOutput: true);
        }

        if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var c in content.EnumerateArray())
        {
            switch (Str(c, "type"))
            {
                case "text" when Str(c, "text") is { Length: > 0 } t:
                    events.Add(new ModelEvent.TextDelta(t));
                    break;
                case "tool_use":
                    var id = Str(c, "id") ?? "call";
                    var name = Str(c, "name") ?? "";
                    if (name.StartsWith(ClaudeCodeArguments.ToolPrefix, StringComparison.Ordinal))
                    {
                        name = name[ClaudeCodeArguments.ToolPrefix.Length..];
                    }

                    if (!offeredTools.Contains(name))
                    {
                        events.Add(new ModelEvent.Failure("malformed_tool_json", $"the model called a tool that was not offered: {Truncate(name)}"));
                        break;
                    }

                    events.Add(new ModelEvent.ToolCallStart(id, name));
                    events.Add(new ModelEvent.ToolCallArgsDelta(id, c.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}"));
                    events.Add(new ModelEvent.ToolCallEnd(id));
                    break;
                default:
                    break;
            }
        }

        if (_stopReason == "tool_use")
        {
            Halt = true;
        }
    }

    private void OnResult(JsonElement root, List<ModelEvent> events)
    {
        ResultSeen = true;
        if (root.TryGetProperty("usage", out var u))
        {
            // The final counts replace the running ones.
            _input = _output = _cacheRead = _cacheWrite5m = _cacheWrite1h = 0;
            ReadUsage(u, includeOutput: true);
        }

        var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
        var sub = Str(root, "subtype");
        if (Halt && sub == "error_max_turns")
        {
            return;
        }

        if (isError || (sub is not null && sub.StartsWith("error", StringComparison.Ordinal)))
        {
            ResultFailure = RateLimitClassifier.Classify(_lastApiRetryError, Str(root, "result") ?? sub, time.GetUtcNow(), options);
        }
    }

    private void ReadUsage(JsonElement u, bool includeOutput)
    {
        _input = Max(_input, Num(u, "input_tokens"));
        _cacheRead = Max(_cacheRead, Num(u, "cache_read_input_tokens"));
        if (u.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
        {
            _cacheWrite5m = Max(_cacheWrite5m, Num(cc, "ephemeral_5m_input_tokens"));
            _cacheWrite1h = Max(_cacheWrite1h, Num(cc, "ephemeral_1h_input_tokens"));
        }
        else
        {
            _cacheWrite5m = Max(_cacheWrite5m, Num(u, "cache_creation_input_tokens"));
        }

        if (includeOutput)
        {
            _output = Max(_output, Num(u, "output_tokens"));
        }
    }

    private static long Max(long a, long b) => a > b ? a : b;

    private static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80];

    private static StopReason Map(string? reason) => reason switch
    {
        "end_turn" or "stop_sequence" => StopReason.EndTurn,
        "tool_use" => StopReason.ToolUse,
        "max_tokens" => StopReason.MaxTokens,
        "refusal" => StopReason.ContentFilter,
        _ => StopReason.Other,
    };

    private sealed record Block(string Kind, string? ToolId);
}
