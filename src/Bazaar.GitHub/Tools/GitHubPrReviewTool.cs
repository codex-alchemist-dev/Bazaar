using System.Text.Json;
using System.Text.Json.Nodes;
using Bazaar.Contracts.Gateway;
using Bazaar.GitHub.Adapter;
using Bazaar.GitHub.Auth;
using Bazaar.Server.Agents.Turns.Tools;

namespace Bazaar.GitHub.Tools;

/// <summary>
/// <c>github.pr_review</c> (doc 21, Agent tools [PROPOSED]): posts a review under the calling agent's own App. Only COMMENT and
/// REQUEST_CHANGES are accepted; APPROVE and anything else is refused before GitHub is touched, as for the client <c>pr.review</c>
/// (reviewer approval is data, not merge authority). There is no merge path here. A moved head is reported stale and nothing is posted.
/// </summary>
public sealed class GitHubPrReviewTool(IGitHubAdapter adapter, IGitHubTokenProvider tokens, IGitHubAgentInstallations installations) : IContextualTool
{
    /// <summary>The exact registered name.</summary>
    public const string ToolName = "github.pr_review";

    /// <summary>What a model is told about the tool.</summary>
    public static ModelToolDefinition Definition { get; } = new(
        ToolName,
        "Posts a review on a pull request under your own GitHub App. The event is COMMENT or REQUEST_CHANGES; approval is not available, and nothing here merges. " +
        "Pass the head sha you reviewed (from github.pr_read); if the head has moved, nothing is posted and the current sha is returned.",
        """
        {
          "type": "object",
          "properties": {
            "owner": { "type": "string", "maxLength": 39, "description": "Repository owner (account or organisation)." },
            "repo": { "type": "string", "maxLength": 100, "description": "Repository name." },
            "number": { "type": "integer", "minimum": 1, "description": "Pull request number." },
            "headSha": { "type": "string", "minLength": 40, "maxLength": 40, "description": "The 40-character head sha you reviewed." },
            "event": { "type": "string", "enum": ["COMMENT", "REQUEST_CHANGES"], "description": "The review event." },
            "body": { "type": "string", "maxLength": 20000, "description": "The review text." }
          },
          "required": ["owner", "repo", "number", "headSha", "event", "body"],
          "additionalProperties": false
        }
        """);

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(JsonElement args, ToolCallContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!GitHubToolSupport.TryReadPr(args, out var owner, out var repo, out var number, out var refusal))
        {
            return refusal!.ToJsonString();
        }

        if (!GitHubToolSupport.TryHeadSha(args, out var headSha))
        {
            return GitHubToolSupport.Refuse("invalid_arguments", "headSha must be a 40-character hex sha.").ToJsonString();
        }

        if (!args.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.String)
        {
            return GitHubToolSupport.Refuse("invalid_arguments", "event must be COMMENT or REQUEST_CHANGES.").ToJsonString();
        }

        var githubEvent = ev.GetString();
        if (githubEvent is not ("COMMENT" or "REQUEST_CHANGES"))
        {
            return GitHubToolSupport.Refuse("refused", "Only COMMENT and REQUEST_CHANGES are accepted. Approval is not available to an agent and nothing here merges.").ToJsonString();
        }

        if (!args.TryGetProperty("body", out var bodyElement) || bodyElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(bodyElement.GetString()) || bodyElement.GetString()!.Length > GitHubToolSupport.MaxBodyChars)
        {
            return GitHubToolSupport.Refuse("invalid_arguments", $"body must be non-empty text of at most {GitHubToolSupport.MaxBodyChars} characters.").ToJsonString();
        }

        try
        {
            var (installation, failure) = await GitHubToolSupport.ResolveAsync(installations, adapter, tokens, context.AgentId, owner, repo, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                return failure.ToJsonString();
            }

            var pr = new PrRef(installation, owner, repo, number);
            var current = await adapter.GetHeadShaAsync(pr, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current, headSha, StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject { ["ok"] = false, ["status"] = "stale", ["headSha"] = current }.ToJsonString();
            }

            await adapter.PostReviewAsync(pr, new ReviewRequest(headSha, githubEvent, bodyElement.GetString()!), cancellationToken).ConfigureAwait(false);
            return new JsonObject { ["ok"] = true, ["status"] = "applied", ["headSha"] = current }.ToJsonString();
        }
        catch (Exception e) when (GitHubToolSupport.IsHandled(e))
        {
            return GitHubToolSupport.Failure(e).ToJsonString();
        }
    }
}
