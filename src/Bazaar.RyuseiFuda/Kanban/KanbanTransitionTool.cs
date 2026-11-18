using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.Server.Kanban;

/// <summary>
/// The <c>kanban.transition</c> agent tool (doc 12, Interception at tool dispatch). It is the only agent path that moves a
/// card: it calls <see cref="TaskService.TransitionAsync"/> as the calling agent, so the same guard, version check and
/// refusal rules apply as for every other caller. Agents never write task tables. <see cref="KanbanComponent"/> registers
/// it with the turn path's tool registry, together with its policy entry.
/// </summary>
public sealed class KanbanTransitionTool(TaskService tasks) : IContextualTool
{
    /// <summary>The exact registered name.</summary>
    public const string ToolName = "kanban.transition";

    /// <summary>What a model is told about the tool. The schema matches the checks in <see cref="ExecuteAsync(JsonElement, ToolCallContext, CancellationToken)"/>.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        ToolName,
        "Moves a task card to another column on the board. The move is checked by the board's rules: an unfinished blocker, " +
        "a missing link or a missing permission refuses it, and the result says why. Pass the card's current version; a " +
        "conflict result returns the newer version to retry with.",
        """
        {
          "type": "object",
          "properties": {
            "taskId": { "type": "string", "format": "uuid", "description": "The task to move." },
            "to": { "type": "string", "enum": ["backlog", "ready", "in_progress", "review", "done"], "description": "The column to move the task to." },
            "expectedVersion": { "type": "integer", "minimum": 1, "description": "The task's version as last read." },
            "reason": { "type": "string", "description": "Why the task moves. Some moves require it." }
          },
          "required": ["taskId", "to", "expectedVersion"],
          "additionalProperties": false
        }
        """);

    private readonly TaskService _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (args.ValueKind != JsonValueKind.Object)
        {
            return Refusal("Arguments must be a JSON object.").ToJsonString();
        }

        if (!TryGuid(args, "taskId", out var taskId))
        {
            return Refusal("taskId must be a GUID string.").ToJsonString();
        }

        if (!TryState(args, "to", out var to))
        {
            return Refusal("to must be one of backlog, ready, in_progress, review, done.").ToJsonString();
        }

        if (!args.TryGetProperty("expectedVersion", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number ||
            !versionElement.TryGetInt64(out var expectedVersion) ||
            expectedVersion < 1)
        {
            return Refusal("expectedVersion must be a positive integer.").ToJsonString();
        }

        string? reason = null;
        if (args.TryGetProperty("reason", out var reasonElement) && reasonElement.ValueKind != JsonValueKind.Null)
        {
            if (reasonElement.ValueKind != JsonValueKind.String)
            {
                return Refusal("reason must be a string.").ToJsonString();
            }

            reason = reasonElement.GetString();
        }

        // The actor is the calling agent, never a value from the model's arguments.
        var actor = new TaskActor(TaskActorKind.Agent, context.AgentId);
        var result = await _tasks.TransitionAsync(taskId, to, actor, reason, expectedVersion, cancellationToken).ConfigureAwait(false);
        return Shape(result).ToJsonString();
    }

    /// <summary>Maps a transition result onto the tool's JSON. Refusals carry the blocker ids so the agent can post in their GCs.</summary>
    public static JsonObject Shape(TransitionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var node = new JsonObject();
        switch (result.Status)
        {
            case TransitionStatus.Applied:
                node["ok"] = true;
                node["status"] = "applied";
                node["version"] = result.Version;
                break;
            case TransitionStatus.Conflict:
                node["ok"] = false;
                node["status"] = "conflict";
                node["currentVersion"] = result.Version;
                break;
            case TransitionStatus.Refused:
                node["ok"] = false;
                node["status"] = "refused";
                node["outcome"] = OutcomeText(result.Outcome);
                if (result.UnfinishedBlockers.Count > 0)
                {
                    var blockers = new JsonArray();
                    foreach (var id in result.UnfinishedBlockers)
                    {
                        blockers.Add(id.ToString("D"));
                    }

                    node["unfinishedBlockers"] = blockers;
                }

                break;
            default:
                node["ok"] = false;
                node["status"] = "invalid";
                node["outcome"] = OutcomeText(result.Outcome);
                break;
        }

        return node;
    }

    private static string OutcomeText(TransitionOutcome outcome) => outcome switch
    {
        TransitionOutcome.Allowed => "allowed",
        TransitionOutcome.NotATransition => "not_a_transition",
        TransitionOutcome.UnknownTask => "unknown_task",
        TransitionOutcome.NotAuthorised => "not_authorised",
        TransitionOutcome.MissingReason => "missing_reason",
        TransitionOutcome.NeedsLinks => "needs_links",
        TransitionOutcome.PrNotMerged => "pr_not_merged",
        TransitionOutcome.GateNotSatisfied => "gate_not_satisfied",
        TransitionOutcome.BlockersUnfinished => "blockers_unfinished",
        _ => "unknown",
    };

    private static JsonObject Refusal(string message) => new()
    {
        ["ok"] = false,
        ["status"] = "invalid_arguments",
        ["message"] = message,
    };

    private static bool TryGuid(JsonElement args, string name, out Guid value)
    {
        value = default;
        return args.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value);
    }

    private static bool TryState(JsonElement args, string name, out TaskState state)
    {
        state = default;
        if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (property.GetString())
        {
            case "backlog": state = TaskState.Backlog; return true;
            case "ready": state = TaskState.Ready; return true;
            case "in_progress": state = TaskState.InProgress; return true;
            case "review": state = TaskState.Review; return true;
            case "done": state = TaskState.Done; return true;
            default: return false;
        }
    }
}
