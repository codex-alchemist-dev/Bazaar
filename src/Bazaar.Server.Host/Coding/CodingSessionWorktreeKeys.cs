using System.Collections.Concurrent;

namespace Bazaar.Server.Host.Coding;

/// <summary>Which task worktree key a running session writes in. Filled when a task session starts (doc 17, "Default repo and per-task worktree flow").</summary>
public interface ICodingSessionWorktreeKeys
{
    /// <summary>The worktree key held for the session, or null.</summary>
    string? KeyFor(Guid sessionId);
}

/// <summary>In-process <see cref="ICodingSessionWorktreeKeys"/>.</summary>
public sealed class CodingSessionWorktreeKeys : ICodingSessionWorktreeKeys
{
    private readonly ConcurrentDictionary<Guid, string> _keys = new();

    /// <inheritdoc />
    public string? KeyFor(Guid sessionId) => _keys.TryGetValue(sessionId, out var key) ? key : null;

    /// <summary>Records the key for the session, replacing any earlier one.</summary>
    public void Set(Guid sessionId, string worktreeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeKey);
        _keys[sessionId] = worktreeKey;
    }

    /// <summary>Forgets the session's key.</summary>
    public void Remove(Guid sessionId) => _keys.TryRemove(sessionId, out _);
}
