using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingRunner;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// The host's <see cref="ICodingToolHostContext"/>: worktrees from the <see cref="CodingWorktreeRegistry"/>, agent facts from
/// <see cref="ICodingAgentFacts"/>, parent limits from <see cref="ICodingSessionParents"/>. The parent limits are returned only
/// when they name the calling session, so a handler can never spawn under another session's limits.
/// </summary>
public sealed class CodingToolHostContext(
    CodingWorktreeRegistry worktrees,
    ICodingAgentFacts agents,
    ICodingSessionParents parents) : ICodingToolHostContext
{
    /// <inheritdoc />
    public CodingWorktree? FindWorktree(string worktreeKey) => worktrees.Find(worktreeKey);

    /// <inheritdoc />
    public ModelIdentity CommitModelFor(string agentId) => agents.CommitModelFor(agentId);

    /// <inheritdoc />
    public string ModelIdFor(string agentId) => agents.ModelIdFor(agentId);

    /// <inheritdoc />
    public ParentContext? ParentFor(CodingToolCall toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        var parent = parents.Find(toolCall.SessionId);
        return parent is not null && string.Equals(parent.ParentId, toolCall.SessionId, StringComparison.Ordinal) ? parent : null;
    }
}
