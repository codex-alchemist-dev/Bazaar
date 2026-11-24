using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>Shared worktree lookup and git failure mapping for the git handlers.</summary>
internal static class GitToolSupport
{
    /// <summary>Exit code when a git tool names no worktree the host holds (usage error).</summary>
    public const int NoWorktreeExitCode = 64;

    /// <summary>
    /// Finds the call's worktree through the host. A call without a key, or with a key the host does not hold, gets a structured
    /// error and no worktree.
    /// </summary>
    public static CodingWorktree? Resolve(CodingToolCall toolCall, ICodingToolHostContext host, out CodingToolOutput? error)
    {
        var key = toolCall.WorktreeKey;
        var worktree = string.IsNullOrWhiteSpace(key) ? null : host.FindWorktree(key);
        if (worktree is null)
        {
            error = CodingToolResults.Failure("no_worktree", "The call names no worktree this agent holds.", NoWorktreeExitCode);
            return null;
        }

        error = null;
        return worktree;
    }

    /// <summary>
    /// Maps a git-service refusal to a structured error. The code is the error kind; the message is the service's fixed text, which
    /// never carries caller input.
    /// </summary>
    public static CodingToolOutput FromGit(CodingGitException ex) =>
        CodingToolResults.Failure("git_" + CodingToolResults.SnakeCase(ex.Error.ToString()), ex.Message);
}
