using Bazaar.Server.CodingGit;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>
/// The server-known facts the coding handlers need and must never take from the model's arguments: which worktree a key names,
/// which model an agent runs as, and the parent limits of a session. The host binds it; a missing binding fails resolution.
/// </summary>
public interface ICodingToolHostContext
{
    /// <summary>Returns the worktree held under <paramref name="worktreeKey"/>, or null when the key names none.</summary>
    CodingWorktree? FindWorktree(string worktreeKey);

    /// <summary>The model identity credited as <c>Co-Authored-By</c> on the agent's commits.</summary>
    ModelIdentity CommitModelFor(string agentId);

    /// <summary>The agent's own model id, used to pick a reviewer from another family where one exists.</summary>
    string ModelIdFor(string agentId);

    /// <summary>The parent limits for spawning a subagent from this call's session, or null when the session has none.</summary>
    ParentContext? ParentFor(CodingToolCall toolCall);
}
