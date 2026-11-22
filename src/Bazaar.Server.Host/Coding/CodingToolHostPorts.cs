using System.Collections.Concurrent;
using Bazaar.Server.CodingEdit;
using Bazaar.Server.CodingGit;
using Bazaar.Server.Subagents;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Facts about an agent that the coding tool handlers need and must never read from the model's arguments (doc 17, "Tool
/// handlers", decision 2). No production source exists yet: the Agents and Identity sides own them, so the host binds this
/// port and the harness start check refuses to start without it.
/// </summary>
public interface ICodingAgentFacts
{
    /// <summary>The model identity credited as <c>Co-Authored-By</c> on the agent's commits.</summary>
    ModelIdentity CommitModelFor(string agentId);

    /// <summary>The agent's own model id, used to pick a reviewer from another family where one exists.</summary>
    string ModelIdFor(string agentId);

    /// <summary>The edit format the agent's model whitelist allows.</summary>
    EditFormat EditFormatFor(string agentId);
}

/// <summary>
/// Resolves a coding session to the limits a subagent it spawns must stay inside. The returned
/// <see cref="ParentContext.ParentId"/> must equal the session id, or the spawn fails with <c>parent_mismatch</c>.
/// No production source exists yet; the Agents runtime owns it (doc 06).
/// </summary>
public interface ICodingSessionParents
{
    /// <summary>Returns the limits for <paramref name="sessionId"/>, or null when the session is unknown.</summary>
    ParentContext? Find(string sessionId);
}

/// <summary>
/// The worktrees held by live coding sessions, by worktree key. The key is the coding session id (one worktree per session,
/// doc 17: one writer per worktree). The flow that starts a coding task creates the worktree through
/// <see cref="CodingGitService"/> and registers it here; a key nothing registered resolves to null, so a handler fails with
/// a usage error rather than running in a directory the host does not hold.
/// </summary>
public sealed class CodingWorktreeRegistry
{
    private readonly ConcurrentDictionary<string, CodingWorktree> _worktrees = new(StringComparer.Ordinal);

    /// <summary>Registers the worktree under the key. Returns false, and keeps the first, when the key is already held.</summary>
    public bool TryRegister(string worktreeKey, CodingWorktree worktree)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeKey);
        ArgumentNullException.ThrowIfNull(worktree);
        return _worktrees.TryAdd(worktreeKey, worktree);
    }

    /// <summary>Removes the key. The caller disposes the worktree, so the host locks are released by whoever created it.</summary>
    public bool Remove(string worktreeKey) => _worktrees.TryRemove(worktreeKey, out _);

    /// <summary>Returns the worktree held under the key, or null.</summary>
    public CodingWorktree? Find(string worktreeKey) => _worktrees.TryGetValue(worktreeKey, out var worktree) ? worktree : null;
}
