using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingReview;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>review</c> tool: runs one read-only review of a pull request and records the outcome (doc 17, review flow). It posts the
/// report to the PR's group chat and sets the approval flag, which is never merge authority, so it is classed Post. Arguments:
/// <c>repository</c>, <c>number</c>, <c>base_commit</c> and <c>head_commit</c> (hex, 7 to 64 characters). The author model comes
/// from the host, never from the arguments, so the caller cannot choose a reviewer pool.
/// </summary>
public sealed class ReviewToolHandler(ReviewService review, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "review";

    private const int _maxRepository = 200;
    private const int _maxCommit = 64;
    private const int _minCommit = 7;

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Post;

    /// <inheritdoc />
    public bool WritesWorktree => false;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        ReviewPullRequestRef pullRequest;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "repository", "number", "base_commit", "head_commit");
            pullRequest = new ReviewPullRequestRef(
                args.RequiredString("repository", _maxRepository),
                args.RequiredInt("number", 1, int.MaxValue),
                RequiredCommit(args, "base_commit"),
                RequiredCommit(args, "head_commit"));
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        ReviewOutcome outcome;
        try
        {
            outcome = await review.ReviewAsync(pullRequest, host.ModelIdFor(toolCall.AgentId), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return CodingToolResults.Failure(CodingToolResults.MalformedCode, "The review request is not valid.", CodingToolResults.MalformedExitCode);
        }

        // A review that did not run is a failure, not a verdict: the change is not approved.
        if (outcome.Verdict == ReviewVerdict.NotCompleted)
        {
            return CodingToolResults.Failure("review_not_completed", "No reviewer ran, so the change is not approved.");
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteString("verdict", CodingToolResults.SnakeCase(outcome.Verdict.ToString()));
            w.WriteString("reviewer_model_id", outcome.ReviewerModelId);
            w.WriteBoolean("same_family", outcome.SameFamily);
            w.WriteBoolean("context_truncated", outcome.ContextTruncated);
        }));
    }

    private static string RequiredCommit(CodingToolArgs args, string name)
    {
        var value = args.RequiredString(name, _maxCommit);
        if (value.Length < _minCommit || !value.All(Uri.IsHexDigit))
        {
            throw new CodingToolArgumentException(name, $"must be a hex commit id of {_minCommit} to {_maxCommit} characters");
        }

        return value;
    }
}
