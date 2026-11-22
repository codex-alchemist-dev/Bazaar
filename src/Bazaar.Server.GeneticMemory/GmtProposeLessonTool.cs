using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GeneticMemory.Abstractions;

namespace Bazaar.Server.GeneticMemory;

/// <summary>
/// [PROPOSED] <c>gmt.propose_lesson</c>: a child records one lesson it wants its parent to have (doc 25, Genetic Memory Transfer).
/// The tool only records. Nothing is written to memory: the lesson waits in the review queue until a reviewer accepts it. It is
/// registered only when <c>Bazaar:GeneticMemory:ProposalTool:Enabled</c> is on, and it refuses every call unless the host can resolve
/// the caller's session (<see cref="IGmtChildContext"/>), so an install without that wiring records nothing. The tool is offered
/// to a turn only through its tool allowlist, so a parent that was not given it cannot call it.
/// </summary>
public sealed class GmtProposeLessonTool(GmtProposalStore store, GmtOptions options, IGmtChildContext? children = null) : IContextualTool
{
    /// <summary>What a model is told about the tool.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        GmtToolNames.ProposeLesson,
        "Records one lesson you learned for the agent that started you, to be reviewed before it is kept. Write a short, general " +
        "procedure or a failure to avoid. Do not name people, customers or projects, and never include keys or tokens. It is only " +
        "a proposal: nothing changes until a reviewer accepts it.",
        """
        {
          "type": "object",
          "properties": {
            "kind": { "type": "string", "enum": ["procedure", "failure"], "description": "A procedure to follow, or a failure to avoid." },
            "title": { "type": "string", "maxLength": 120, "description": "A short title." },
            "body": { "type": "string", "maxLength": 4000, "description": "The lesson, in plain general terms." }
          },
          "required": ["kind", "title", "body"],
          "additionalProperties": false
        }
        """);

    private const int _maxTitle = 120;
    private const int _maxBody = 4000;

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (children is null || await children.ResolveAsync(context.AgentId, context.SessionId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Refusal("Lesson recording is not available for this session.");
        }

        if (args.ValueKind != JsonValueKind.Object)
        {
            return Refusal("Arguments must be a JSON object.");
        }

        var kind = Text(args, "kind") switch
        {
            "procedure" => (GmtLessonKind?)GmtLessonKind.Procedure,
            "failure" => GmtLessonKind.Failure,
            _ => null,
        };
        if (kind is null)
        {
            return Refusal("kind must be procedure or failure.");
        }

        var title = Text(args, "title")?.Trim();
        var body = Text(args, "body")?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > _maxTitle || string.IsNullOrEmpty(body) || body.Length > _maxBody)
        {
            return Refusal($"title must be 1 to {_maxTitle} characters and body 1 to {_maxBody}.");
        }

        // Only lessons can be recorded here. Tool grants, charter text and everything else have no path through this tool.
        if (!store.TryAdd(context.TurnId, new GmtProposal(GmtContentClass.Lesson, kind.Value, title, body), options.MaxProposalsPerChild))
        {
            return Refusal($"You may record at most {options.MaxProposalsPerChild} lessons.");
        }

        return new JsonObject { ["recorded"] = true }.ToJsonString();
    }

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Refusal(string message) => new JsonObject { ["recorded"] = false, ["error"] = message }.ToJsonString();
}
