using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Community.Drafts;
using Bazaar.Community.Gates;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.Community;

namespace Bazaar.Community.Tools;

/// <summary>
/// The <c>community.propose_post</c> agent tool (doc 36, work package 8). It is the only agent path into the draft-review flow: it
/// stores a Pending draft through <see cref="ICommunityDraftCreator"/> and returns the draft id and the gate failures. Nothing is
/// posted. The sponsor reviews the draft and signs the decision on a client; the post happens only on that signed approval.
/// The calling agent, its sponsor and its profile come from the dispatcher's caller and <see cref="ICommunityAgentIdentitySource"/>,
/// never from the model's arguments, and the model never writes the disclosure footer (the creator appends it).
/// </summary>
public sealed class CommunityProposePostTool(
    ICommunityDraftCreator creator,
    ICommunityAgentIdentitySource identities,
    ICommunityTaintSourceReader? taint = null) : IContextualTool
{
    /// <summary>The exact registered name.</summary>
    public const string ToolName = "community.propose_post";

    private static readonly Dictionary<string, CommunityAction> _actions = new(StringComparer.Ordinal)
    {
        ["reply"] = CommunityAction.ReplyApprovedThread,
        ["post"] = CommunityAction.NewPost,
        ["issue"] = CommunityAction.NewIssue,
        ["pull_request"] = CommunityAction.NewPullRequest,
    };

    /// <summary>What a model is told about the tool.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        ToolName,
        "Proposes a public post on an external platform for your sponsor to review. Nothing is posted until the sponsor approves it " +
        "on their device. Write only your own text: the disclosure footer is added for you. Run a duplicate search first and list " +
        "its queries. A bug report needs the output of a minimal example and the tool versions.",
        """
        {
          "type": "object",
          "properties": {
            "platform": { "type": "string", "description": "Platform key, for example github." },
            "action": { "type": "string", "enum": ["reply", "post", "issue", "pull_request"] },
            "kind": { "type": "string", "enum": ["issue", "pr", "thread", "comment"], "description": "The item kind the post becomes." },
            "target": { "type": "string", "description": "owner/repo or the thread reference the post goes to." },
            "title": { "type": "string", "description": "Title, for an issue or pull request." },
            "body": { "type": "string", "description": "Your text, without a disclosure footer." },
            "headBranch": { "type": "string", "description": "Head branch, for a pull request." },
            "baseBranch": { "type": "string", "description": "Base branch, for a pull request." },
            "duplicateSearchQueries": { "type": "array", "items": { "type": "string" } },
            "duplicateSearchLinksChecked": { "type": "array", "items": { "type": "string" } },
            "relatedLinks": { "type": "array", "items": { "type": "string" } },
            "isBugReport": { "type": "boolean" },
            "exampleOutput": { "type": "string" },
            "exampleVersions": { "type": "string" }
          },
          "required": ["platform", "action", "kind", "target", "body"],
          "additionalProperties": false
        }
        """);

    private readonly ICommunityDraftCreator _creator = creator ?? throw new ArgumentNullException(nameof(creator));
    private readonly ICommunityTaintSourceReader? _taint = taint;
    private readonly ICommunityAgentIdentitySource _identities = identities ?? throw new ArgumentNullException(nameof(identities));

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (args.ValueKind != JsonValueKind.Object) return Refusal("bad_request", "Arguments must be a JSON object.");

        if (!TryString(args, "platform", required: true, out var platform)
            || !TryString(args, "kind", required: true, out var kind)
            || !TryString(args, "target", required: true, out var target)
            || !TryString(args, "body", required: true, out var body)
            || !TryString(args, "action", required: true, out var actionText)
            || !TryString(args, "title", required: false, out var title)
            || !TryString(args, "headBranch", required: false, out var head)
            || !TryString(args, "baseBranch", required: false, out var baseBranch)
            || !TryString(args, "exampleOutput", required: false, out var exampleOutput)
            || !TryString(args, "exampleVersions", required: false, out var exampleVersions)
            || !TryList(args, "duplicateSearchQueries", out var queries)
            || !TryList(args, "duplicateSearchLinksChecked", out var checkedLinks)
            || !TryList(args, "relatedLinks", out var related)
            || !TryBool(args, "isBugReport", out var isBug))
        {
            return Refusal("bad_request", "An argument is missing or has the wrong type.");
        }

        if (!_actions.TryGetValue(actionText!, out var action)) return Refusal("unsupported_action", "action must be reply, post, issue or pull_request.");

        var identity = await _identities.FindAsync(context.AgentId, platform!, cancellationToken).ConfigureAwait(false);
        if (identity is null) return Refusal("no_profile", "You have no disclosed profile on that platform, so you cannot propose a post there.");

        var taintJson = _taint is null ? "[]" : await _taint.ReadJsonAsync(context.SessionId, cancellationToken).ConfigureAwait(false);
        var proposal = new CommunityDraftProposal(
            context.AgentId.ToString("D"), identity.SponsorId, action, identity.Profile,
            new DraftPost(platform!, kind!, body!, queries, related, exampleOutput, exampleVersions, isBug, checkedLinks),
            target!, title, head, baseBranch, taintJson);

        var result = await _creator.CreateAsync(proposal, cancellationToken).ConfigureAwait(false);
        if (!result.Created) return Refusal(result.Code, "The draft was not stored.");

        var node = new JsonObject { ["ok"] = true, ["status"] = "pending_sponsor_review", ["draftId"] = result.Card!.DraftId.ToString("D") };
        var failures = new JsonArray();
        foreach (var g in result.Card.Gates)
        {
            if (!g.Passed) failures.Add(g.Gate);
        }

        node["gateFailures"] = failures;
        return node.ToJsonString();
    }

    private static string Refusal(string code, string message) =>
        new JsonObject { ["ok"] = false, ["status"] = "refused", ["code"] = code, ["message"] = message }.ToJsonString();

    private static bool TryString(JsonElement args, string name, bool required, out string? value)
    {
        value = null;
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return !required;
        if (e.ValueKind != JsonValueKind.String) return false;
        value = e.GetString();
        return !required || !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryList(JsonElement args, string name, out IReadOnlyList<string> value)
    {
        value = [];
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return true;
        if (e.ValueKind != JsonValueKind.Array) return false;
        var list = new List<string>();
        foreach (var item in e.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) return false;
            list.Add(item.GetString()!);
        }

        value = list;
        return true;
    }

    private static bool TryBool(JsonElement args, string name, out bool value)
    {
        value = false;
        if (!args.TryGetProperty(name, out var e) || e.ValueKind == JsonValueKind.Null) return true;
        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = e.GetBoolean();
        return true;
    }
}
