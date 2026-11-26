using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>git_push_draft_pr</c> tool: pushes the worktree's <c>agent/*</c> branch and opens a draft pull request, after the push
/// gate allows it (doc 17: push and draft PR are Post, host-side through doc 21). It does not change the worktree, so it is not
/// a runner writer; the service serialises it on the worktree itself. Arguments: <c>title</c> (required), <c>body</c> and
/// <c>branch</c> (optional; any value other than the worktree's own branch is refused by the service). Gate refusals come back
/// as <c>git_push_needs_approval</c> or <c>git_push_denied</c>, and nothing is pushed.
/// </summary>
public sealed class GitPushDraftPrToolHandler(CodingGitService git, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "git_push_draft_pr";

    private const int _maxTitle = 1000;
    private const int _maxBody = 60_000;
    private const int _maxBranch = 200;

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

        PushDraftPrInput input;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "title", "body", "branch");
            input = new PushDraftPrInput(
                args.RequiredString("title", _maxTitle),
                args.OptionalString("body", _maxBody, allowEmpty: true) ?? string.Empty,
                args.OptionalString("branch", _maxBranch));
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        var worktree = GitToolSupport.Resolve(toolCall, host, out var error);
        if (worktree is null)
        {
            return error!;
        }

        PullRequestRef pr;
        try
        {
            pr = await git.PushDraftPrAsync(worktree, input, cancellationToken).ConfigureAwait(false);
        }
        catch (CodingGitException ex)
        {
            return GitToolSupport.FromGit(ex);
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w =>
        {
            w.WriteNumber("number", pr.Number);
            w.WriteString("url", pr.Url);
            w.WriteBoolean("is_draft", pr.IsDraft);
        }));
    }
}
