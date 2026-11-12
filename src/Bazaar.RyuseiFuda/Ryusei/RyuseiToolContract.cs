using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>The four agent-callable RyuseiFuda tools (doc 38, WP-R9).</summary>
public enum RyuseiToolName
{
    Schedule,
    List,
    Pause,
    Cancel,
}

/// <summary>
/// A parsed tool request, or the reason it was refused. Exactly one of <see cref="Request"/> and <see cref="Refusal"/> is set.
/// </summary>
public sealed record RyuseiToolParse<T>(T? Request, string? Refusal) where T : class
{
    public RyuseiToolParse(T request)
        : this(request, null)
    {
    }

    public bool IsValid => Request is not null;
}

/// <summary>A parsed <c>ryusei.schedule</c> call. <see cref="Spec"/> already matches <see cref="Kind"/>.</summary>
public sealed record RyuseiScheduleToolRequest(
    RyuseiTriggerKind Kind,
    RyuseiTriggerSpec Spec,
    string Purpose,
    IReadOnlyList<Guid> TargetAgentIds,
    RyuseiPingMode PingMode,
    Guid? AttachGcId);

/// <summary>A parsed <c>ryusei.pause</c> or <c>ryusei.cancel</c> call.</summary>
public sealed record RyuseiTriggerToolRequest(Guid TriggerId);

/// <summary>A parsed <c>ryusei.list</c> call. A null <see cref="State"/> lists every state.</summary>
public sealed record RyuseiListToolRequest(RyuseiTriggerState? State, int Limit);

/// <summary>
/// Argument parsing and result shaping for the four RyuseiFuda agent tools (doc 38, Authority and WP-R9). The functions are
/// pure: they read JSON and build JSON, and touch no database and no clock.
/// </summary>
/// <remarks>
/// Parsing checks shape only: required fields, value types and wire names. Authority, caps, predicate validity and the
/// target directory are left to <see cref="RyuseiTriggerService"/>, which already applies them and writes the audit row,
/// so the rules are not repeated here. A refusal from this class means the arguments were malformed, not that the action
/// is forbidden.
/// </remarks>
public static class RyuseiToolContract
{
    public const string ScheduleToolName = "ryusei.schedule";
    public const string ListToolName = "ryusei.list";
    public const string PauseToolName = "ryusei.pause";
    public const string CancelToolName = "ryusei.cancel";

    /// <summary>
    /// What a model is told about the four tools. Each schema matches the parser for that tool in this class; the
    /// <c>spec</c> object is described per kind because its fields depend on <c>kind</c> (<see cref="RyuseiSpecJson"/>).
    /// </summary>
    public static IReadOnlyList<ModelToolDefinition> Definitions { get; } =
    [
        new(
            ScheduleToolName,
            "Schedules a trigger that wakes one or more agents later: at a time (timer), repeatedly (interval), when a " +
            "condition is seen by polling (watcher) or when an event arrives (event). Caps and permissions are checked; a " +
            "refusal says why.",
            """
            {
              "type": "object",
              "properties": {
                "kind": { "type": "string", "enum": ["timer", "interval", "watcher", "event"] },
                "purpose": { "type": "string", "description": "Why the trigger exists. Shown to the woken agents." },
                "targets": { "type": "array", "minItems": 1, "items": { "type": "string", "format": "uuid" }, "description": "The agents to wake." },
                "ping_mode": { "type": "string", "enum": ["none", "attach", "new_gc"], "description": "Where a fire is announced. Defaults to attach when attach_gc_id is given, otherwise none." },
                "attach_gc_id": { "type": "string", "format": "uuid", "description": "The group chat a fire is announced in." },
                "spec": {
                  "type": "object",
                  "description": "Depends on kind. timer: fireAt. interval: every and anchor. watcher: predicate, pollSeconds, debounceSeconds, and optionally expiresAt and maxFires. event: predicate.",
                  "properties": {
                    "fireAt": { "type": "string", "format": "date-time" },
                    "every": { "type": "string", "description": "An ISO 8601 duration, for example PT30M." },
                    "anchor": { "type": "string", "format": "date-time" },
                    "pollSeconds": { "type": "integer" },
                    "debounceSeconds": { "type": "integer" },
                    "expiresAt": { "type": "string", "format": "date-time" },
                    "maxFires": { "type": "integer" },
                    "predicate": {
                      "type": "object",
                      "properties": {
                        "kind": { "type": "string", "enum": ["task.transitioned", "task.due_passed", "task.blocked", "task.unblocked", "pr.merged", "pr.closed", "pr.review_requested", "gc.message", "vault.changed", "council.decision_recorded"] },
                        "gcId": { "type": "string", "format": "uuid" },
                        "authorAgentId": { "type": "string", "format": "uuid" },
                        "pathGlob": { "type": "string" }
                      },
                      "required": ["kind"]
                    }
                  }
                }
              },
              "required": ["kind", "purpose", "targets", "spec"]
            }
            """),
        new(
            ListToolName,
            "Lists the triggers you may see, with their state and next fire time.",
            """
            {
              "type": "object",
              "properties": {
                "state": { "type": "string", "enum": ["active", "paused", "fired", "expired", "cancelled"], "description": "Only triggers in this state." },
                "limit": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Page size. Defaults to 20." }
              }
            }
            """),
        new(
            PauseToolName,
            "Pauses a trigger so it stops firing until it is resumed.",
            """
            {
              "type": "object",
              "properties": { "trigger_id": { "type": "string", "format": "uuid" } },
              "required": ["trigger_id"]
            }
            """),
        new(
            CancelToolName,
            "Cancels a trigger for good.",
            """
            {
              "type": "object",
              "properties": { "trigger_id": { "type": "string", "format": "uuid" } },
              "required": ["trigger_id"]
            }
            """),
    ];

    /// <summary>The default page size for <c>ryusei.list</c>.</summary>
    public const int DefaultListLimit = 20;

    /// <summary>The largest page size <c>ryusei.list</c> accepts.</summary>
    public const int MaxListLimit = 100;

    /// <summary>Finds the tool for an exact, case-sensitive name. Any other name is refused.</summary>
    public static bool TryResolveTool(string? name, out RyuseiToolName tool)
    {
        switch (name)
        {
            case ScheduleToolName:
                tool = RyuseiToolName.Schedule;
                return true;
            case ListToolName:
                tool = RyuseiToolName.List;
                return true;
            case PauseToolName:
                tool = RyuseiToolName.Pause;
                return true;
            case CancelToolName:
                tool = RyuseiToolName.Cancel;
                return true;
            default:
                tool = default;
                return false;
        }
    }

    /// <summary>Parses the arguments of <c>ryusei.schedule</c>.</summary>
    public static RyuseiToolParse<RyuseiScheduleToolRequest> ParseSchedule(JsonElement args)
    {
        if (!TryReadObject(args, out var refusal))
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        var kindText = ReadRequiredString(args, "kind", out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        if (!TryParseKind(kindText!, out var kind))
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, "kind must be one of timer, interval, watcher, event.");
        }

        var purpose = ReadRequiredString(args, "purpose", out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        if (string.IsNullOrWhiteSpace(purpose))
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, "purpose must not be blank.");
        }

        var targets = ReadTargets(args, out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        var attachGcId = ReadOptionalGuid(args, "attach_gc_id", out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        var pingMode = ReadPingMode(args, attachGcId, out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, refusal);
        }

        if (!args.TryGetProperty("spec", out var specElement) || specElement.ValueKind != JsonValueKind.Object)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, "spec is required and must be a JSON object.");
        }

        RyuseiTriggerSpec spec;
        try
        {
            spec = RyuseiSpecJson.Decode(kind, specElement.GetRawText());
        }
        catch (InvalidDataException ex)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, $"spec does not match kind {kindText}: {ex.Message}");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or OverflowException or JsonException)
        {
            return new RyuseiToolParse<RyuseiScheduleToolRequest>(null, $"spec does not match kind {kindText}.");
        }

        return new RyuseiToolParse<RyuseiScheduleToolRequest>(new RyuseiScheduleToolRequest(
            kind, spec, purpose!, targets!, pingMode, attachGcId));
    }

    /// <summary>Parses the arguments of <c>ryusei.list</c>. Both fields are optional.</summary>
    public static RyuseiToolParse<RyuseiListToolRequest> ParseList(JsonElement args)
    {
        if (!TryReadObject(args, out var refusal))
        {
            return new RyuseiToolParse<RyuseiListToolRequest>(null, refusal);
        }

        RyuseiTriggerState? state = null;
        var stateText = ReadOptionalString(args, "state", out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiListToolRequest>(null, refusal);
        }

        if (stateText is not null)
        {
            if (!TryParseState(stateText, out var parsed))
            {
                return new RyuseiToolParse<RyuseiListToolRequest>(null, "state must be one of active, paused, fired, expired, cancelled.");
            }

            state = parsed;
        }

        var limit = DefaultListLimit;
        if (args.TryGetProperty("limit", out var limitElement))
        {
            if (limitElement.ValueKind != JsonValueKind.Number || !limitElement.TryGetInt32(out limit))
            {
                return new RyuseiToolParse<RyuseiListToolRequest>(null, "limit must be a whole number.");
            }

            if (limit < 1 || limit > MaxListLimit)
            {
                return new RyuseiToolParse<RyuseiListToolRequest>(null, $"limit must be between 1 and {MaxListLimit}.");
            }
        }

        return new RyuseiToolParse<RyuseiListToolRequest>(new RyuseiListToolRequest(state, limit));
    }

    /// <summary>Parses the arguments of <c>ryusei.pause</c> and <c>ryusei.cancel</c>, which take one trigger id.</summary>
    public static RyuseiToolParse<RyuseiTriggerToolRequest> ParseTriggerAction(JsonElement args)
    {
        if (!TryReadObject(args, out var refusal))
        {
            return new RyuseiToolParse<RyuseiTriggerToolRequest>(null, refusal);
        }

        var triggerId = ReadRequiredGuid(args, "trigger_id", out refusal);
        if (refusal is not null)
        {
            return new RyuseiToolParse<RyuseiTriggerToolRequest>(null, refusal);
        }

        return new RyuseiToolParse<RyuseiTriggerToolRequest>(new RyuseiTriggerToolRequest(triggerId!.Value));
    }

    /// <summary>The JSON result of a trigger service call, for pause, cancel and schedule.</summary>
    public static JsonObject BuildResult(RyuseiTriggerResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var node = new JsonObject
        {
            ["ok"] = result.IsOk,
            ["status"] = StatusText(result.Status),
        };

        if (result.TriggerId is { } triggerId)
        {
            node["triggerId"] = triggerId.ToString("D");
        }

        if (result.Version is { } version)
        {
            node["version"] = version;
        }

        if (result.Refusal != RyuseiRefusal.None)
        {
            node["refusal"] = result.Refusal.ToString();
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            node["message"] = result.Message;
        }

        return node;
    }

    /// <summary>
    /// The JSON result of <c>ryusei.list</c>: <c>{"ok":true,"count":n,"triggers":[...]}</c>. Each trigger carries its id,
    /// kind, state, purpose, targets, next fire time and creator. The spec is left out, so the result stays small.
    /// </summary>
    public static JsonObject BuildList(IEnumerable<RyuseiTriggerRecord> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);

        var items = new JsonArray();
        foreach (var trigger in triggers)
        {
            var targets = new JsonArray();
            foreach (var agentId in trigger.TargetAgentIds)
            {
                targets.Add(agentId.ToString("D"));
            }

            items.Add(new JsonObject
            {
                ["triggerId"] = trigger.TriggerId.ToString("D"),
                ["kind"] = RyuseiText.ToSql(trigger.Kind),
                ["state"] = RyuseiText.ToSql(trigger.State),
                ["purpose"] = trigger.Purpose,
                ["targets"] = targets,
                ["nextFireAt"] = trigger.NextFireAt is { } next ? next.ToString("O", CultureInfo.InvariantCulture) : null,
                ["createdBy"] = trigger.CreatedBy.ToString("D"),
                ["version"] = trigger.Version,
            });
        }

        return new JsonObject
        {
            ["ok"] = true,
            ["count"] = items.Count,
            ["triggers"] = items,
        };
    }

    /// <summary>The JSON result for a refused call. <paramref name="reason"/> is a fixed sentence, never echoed model text.</summary>
    public static JsonObject BuildRefusal(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new JsonObject
        {
            ["ok"] = false,
            ["status"] = "invalid_arguments",
            ["message"] = reason,
        };
    }

    private static bool TryReadObject(JsonElement args, out string? refusal)
    {
        if (args.ValueKind != JsonValueKind.Object)
        {
            refusal = "The arguments must be a JSON object.";
            return false;
        }

        refusal = null;
        return true;
    }

    private static string? ReadRequiredString(JsonElement args, string name, out string? refusal)
    {
        if (!args.TryGetProperty(name, out var value))
        {
            refusal = $"{name} is required.";
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            refusal = $"{name} must be a string.";
            return null;
        }

        refusal = null;
        return value.GetString();
    }

    private static string? ReadOptionalString(JsonElement args, string name, out string? refusal)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            refusal = null;
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            refusal = $"{name} must be a string.";
            return null;
        }

        refusal = null;
        return value.GetString();
    }

    private static Guid? ReadRequiredGuid(JsonElement args, string name, out string? refusal)
    {
        var text = ReadRequiredString(args, name, out refusal);
        if (refusal is not null)
        {
            return null;
        }

        if (!Guid.TryParse(text, out var guid))
        {
            refusal = $"{name} must be a GUID.";
            return null;
        }

        refusal = null;
        return guid;
    }

    private static Guid? ReadOptionalGuid(JsonElement args, string name, out string? refusal)
    {
        var text = ReadOptionalString(args, name, out refusal);
        if (refusal is not null || text is null)
        {
            return null;
        }

        if (!Guid.TryParse(text, out var guid))
        {
            refusal = $"{name} must be a GUID.";
            return null;
        }

        refusal = null;
        return guid;
    }

    private static List<Guid>? ReadTargets(JsonElement args, out string? refusal)
    {
        if (!args.TryGetProperty("targets", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            refusal = "targets is required and must be an array of agent GUIDs.";
            return null;
        }

        var targets = new List<Guid>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !Guid.TryParse(item.GetString(), out var guid))
            {
                refusal = "Each target must be an agent GUID.";
                return null;
            }

            targets.Add(guid);
        }

        if (targets.Count == 0)
        {
            refusal = "targets must name at least one agent.";
            return null;
        }

        refusal = null;
        return targets;
    }

    private static RyuseiPingMode ReadPingMode(JsonElement args, Guid? attachGcId, out string? refusal)
    {
        var text = ReadOptionalString(args, "ping_mode", out refusal);
        if (refusal is not null)
        {
            return RyuseiPingMode.None;
        }

        // The default follows doc 38: attach when a GC is given, otherwise none.
        if (text is null)
        {
            return attachGcId is null ? RyuseiPingMode.None : RyuseiPingMode.Attach;
        }

        switch (text)
        {
            case "none":
                return RyuseiPingMode.None;
            case "attach":
                return RyuseiPingMode.Attach;
            case "new_gc":
                return RyuseiPingMode.NewGc;
            default:
                refusal = "ping_mode must be one of none, attach, new_gc.";
                return RyuseiPingMode.None;
        }
    }

    private static bool TryParseKind(string text, out RyuseiTriggerKind kind)
    {
        switch (text)
        {
            case "timer":
                kind = RyuseiTriggerKind.Timer;
                return true;
            case "interval":
                kind = RyuseiTriggerKind.Interval;
                return true;
            case "watcher":
                kind = RyuseiTriggerKind.Watcher;
                return true;
            case "event":
                kind = RyuseiTriggerKind.Event;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static bool TryParseState(string text, out RyuseiTriggerState state)
    {
        switch (text)
        {
            case "active":
                state = RyuseiTriggerState.Active;
                return true;
            case "paused":
                state = RyuseiTriggerState.Paused;
                return true;
            case "fired":
                state = RyuseiTriggerState.Fired;
                return true;
            case "expired":
                state = RyuseiTriggerState.Expired;
                return true;
            case "cancelled":
                state = RyuseiTriggerState.Cancelled;
                return true;
            default:
                state = default;
                return false;
        }
    }

    private static string StatusText(RyuseiTriggerStatus status) => status switch
    {
        RyuseiTriggerStatus.Ok => "ok",
        RyuseiTriggerStatus.Refused => "refused",
        RyuseiTriggerStatus.NotFound => "not_found",
        RyuseiTriggerStatus.InvalidState => "invalid_state",
        RyuseiTriggerStatus.Conflict => "conflict",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a trigger status."),
    };
}
