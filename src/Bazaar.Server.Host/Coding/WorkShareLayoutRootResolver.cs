using Bazaar.Server.CodingGit;
using Bazaar.Server.GitHub.Workspace;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Gives <see cref="GitWorkspace"/> the same per-agent roots the harness uses (<see cref="IWorkShareLayout"/>), so the worktrees
/// the workspace creates and validates sit where <see cref="GitWorkspaceCodingGitEngine"/> expects them. The host builds the
/// workspace with this resolver over the same layout it registers for the harness.
/// </summary>
public sealed class WorkShareLayoutRootResolver : IWorkShareRootResolver
{
    private readonly IWorkShareLayout _layout;

    /// <summary>Creates the resolver.</summary>
    public WorkShareLayoutRootResolver(IWorkShareLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layout = layout;
    }

    /// <inheritdoc />
    public string? GetRoot(string agentId) => _layout.GetWorktreeRoot(agentId);
}
