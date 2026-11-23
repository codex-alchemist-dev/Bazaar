using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingRunner.Handlers;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Binds <see cref="IWorktreeFilesSource"/> to the host file system (doc 17, host sources for worktree files). A store is handed out
/// only for an active worktree whose real path lies under the agent's worktree root from <see cref="IWorkShareLayout"/> and not on
/// or around the Passport folder; anything else gives null, so the call fails closed.
/// </summary>
public sealed class WorktreeFilesSource : IWorktreeFilesSource
{
    private readonly IWorkShareLayout _layout;

    /// <summary>Creates the source over the work-share layout.</summary>
    public WorktreeFilesSource(IWorkShareLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    /// <inheritdoc />
    public IWorktreeFiles? FilesFor(CodingWorktree worktree)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        if (!worktree.IsActive)
        {
            return null;
        }

        try
        {
            var root = WorkSharePaths.Canonicalize(_layout.GetWorktreeRoot(worktree.AgentId));
            var passport = WorkSharePaths.Canonicalize(_layout.GetPassportFolder(worktree.AgentId));
            var path = WorkSharePaths.Canonicalize(worktree.Path);
            if (!Directory.Exists(path)
                || !WorkSharePaths.IsEqualOrUnder(path, root)
                || string.Equals(path, root, WorkSharePaths.Comparison)
                || WorkSharePaths.IsEqualOrUnder(path, passport)
                || WorkSharePaths.IsEqualOrUnder(passport, path))
            {
                return null;
            }

            return new ConfinedWorktreeFiles(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A layout that cannot place this agent, or a path that cannot be resolved, is no store (fail closed).
            return null;
        }
    }
}
