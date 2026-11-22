using Bazaar.Server.CodingGit;

namespace Bazaar.Server.CodingWorkspace;

/// <summary>
/// <see cref="IWorkShareLayout"/> over an <see cref="IAgentWorkShareSource"/>. It refuses an agent the source does not know and
/// any pair of paths that is relative, blank, or not separate (doc 17: no worktree on, under or around the Passport folder).
/// </summary>
/// <param name="source">Where the shares come from.</param>
public sealed class WorkShareLayout(IAgentWorkShareSource source) : IWorkShareLayout
{
    private readonly IAgentWorkShareSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <inheritdoc />
    public string GetWorktreeRoot(string agentId) => Resolve(agentId).WorktreeRoot;

    /// <inheritdoc />
    public string GetPassportFolder(string agentId) => Resolve(agentId).PassportFolder;

    private AgentWorkShare Resolve(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new CodingGitException(CodingGitError.InvalidRequest, "An agent id is required.");
        }

        var share = _source.Find(agentId)
            ?? throw new CodingGitException(CodingGitError.WorktreeOutsideWorkShare, "The agent has no work share.");
        if (!IsUsable(share.WorktreeRoot) || !IsUsable(share.PassportFolder))
        {
            throw new CodingGitException(CodingGitError.WorktreeOutsideWorkShare, "The agent's work share is not an absolute path.");
        }

        var work = WorkSharePaths.Canonicalize(share.WorktreeRoot);
        var passport = WorkSharePaths.Canonicalize(share.PassportFolder);
        if (WorkSharePaths.IsEqualOrUnder(work, passport) || WorkSharePaths.IsEqualOrUnder(passport, work))
        {
            throw new CodingGitException(CodingGitError.WorktreeOnPassportFolder, "The work share and the Passport folder must be separate.");
        }

        return new AgentWorkShare(work, passport);
    }

    private static bool IsUsable(string? path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Contains('\0') && Path.IsPathFullyQualified(path);
}
