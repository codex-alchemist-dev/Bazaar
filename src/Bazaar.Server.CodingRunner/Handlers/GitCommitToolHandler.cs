using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The <c>git_commit</c> tool: commits the worktree's pending changes as the agent's identity (doc 17: commit is Exec). It changes
/// the worktree's index and history, so it is a writer and the runner serialises it per worktree. Arguments: <c>subject</c>
/// (required) and <c>body</c>. The model credited as co-author comes from the host, never from the arguments. Trailers, the
/// <c>Signed-off-by</c> ban and secret checks are the service's rules and are not repeated here.
/// </summary>
public sealed class GitCommitToolHandler(CodingGitService git, ICodingToolHostContext host) : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "git_commit";

    private const int _maxSubject = 1000;
    private const int _maxBody = 60_000;

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Exec;

    /// <inheritdoc />
    public bool WritesWorktree => true;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);

        CommitInput input;
        try
        {
            var args = new CodingToolArgs(toolCall.Args, "subject", "body");
            input = new CommitInput(
                args.RequiredString("subject", _maxSubject),
                args.OptionalString("body", _maxBody, allowEmpty: true),
                host.CommitModelFor(toolCall.AgentId));
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

        CommitId commit;
        try
        {
            commit = await git.CommitAsync(worktree, input, cancellationToken).ConfigureAwait(false);
        }
        catch (CodingGitException ex)
        {
            return GitToolSupport.FromGit(ex);
        }
        catch (ArgumentException)
        {
            return CodingToolResults.Failure(CodingToolResults.MalformedCode, "The commit request is not valid.", CodingToolResults.MalformedExitCode);
        }

        return new CodingToolOutput(new ExecOutcome(0), CodingToolResults.Json(w => w.WriteString("sha", commit.Sha)));
    }
}
