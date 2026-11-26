namespace Bazaar.Server.CodingGit;

/// <summary>Why the worktree and push service refused or failed an operation.</summary>
public enum CodingGitError
{
    /// <summary>The branch name is not a valid <c>agent/&lt;task&gt;</c> name.</summary>
    InvalidBranch,

    /// <summary>A request field is missing or malformed.</summary>
    InvalidRequest,

    /// <summary>Another writer holds the worktree or the branch. The caller is not queued.</summary>
    WorktreeBusy,

    /// <summary>The worktree handle was disposed, or its lease expired or was taken over.</summary>
    LeaseLost,

    /// <summary>The worktree path resolves outside the agent's work share.</summary>
    WorktreeOutsideWorkShare,

    /// <summary>The worktree path is, is under, or contains the agent's Passport folder.</summary>
    WorktreeOnPassportFolder,

    /// <summary>A push of the repository's default branch was requested.</summary>
    DefaultBranchRefused,

    /// <summary>A push of a branch that is not <c>agent/*</c>, or not the worktree's own branch, was requested.</summary>
    BranchRefused,

    /// <summary>A <c>Signed-off-by</c> trailer, or another reserved trailer, was supplied.</summary>
    ForbiddenTrailer,

    /// <summary>Text bound for a commit or a pull request looks like it carries a secret.</summary>
    SecretDetected,

    /// <summary>The push gate wants a recorded approval first. Nothing was pushed.</summary>
    PushNeedsApproval,

    /// <summary>The push gate denied the push. Nothing was pushed.</summary>
    PushDenied,

    /// <summary>The push port returned something the service will not accept, such as a non-draft pull request.</summary>
    PushResultInvalid,

    /// <summary>The worktree has no changes to commit.</summary>
    NothingToCommit,
}

/// <summary>
/// Typed failure from the worktree and push service. The message never echoes caller-supplied text, so a rejected
/// secret cannot reach a log through it.
/// </summary>
public sealed class CodingGitException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="error">The failure kind.</param>
    /// <param name="message">A fixed description. Do not pass caller-supplied text.</param>
    public CodingGitException(CodingGitError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>The failure kind.</summary>
    public CodingGitError Error { get; }
}
