using System.Collections.Concurrent;

namespace Bazaar.Server.CodingWorkspace.Fakes;

/// <summary>In-memory <see cref="IAgentWorkShareSource"/>.</summary>
public sealed class InMemoryAgentWorkShareSource : IAgentWorkShareSource
{
    private readonly ConcurrentDictionary<string, AgentWorkShare> _shares = new(StringComparer.Ordinal);

    /// <summary>Sets the shares for an agent.</summary>
    public void Set(string agentId, AgentWorkShare share) => _shares[agentId] = share;

    /// <inheritdoc />
    public AgentWorkShare? Find(string agentId) => _shares.GetValueOrDefault(agentId);
}

/// <summary>In-memory <see cref="ICodingRepoSource"/>.</summary>
public sealed class InMemoryCodingRepoSource : ICodingRepoSource
{
    private readonly ConcurrentDictionary<string, CodingRepoRecord> _repos = new(StringComparer.Ordinal);

    /// <summary>Adds or replaces a repository.</summary>
    public void Set(CodingRepoRecord record) => _repos[record.Repo.Id] = record;

    /// <inheritdoc />
    public ValueTask<CodingRepoRecord?> FindAsync(string repoId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_repos.GetValueOrDefault(repoId));
}

/// <summary>In-memory <see cref="ISubagentParentWorkSource"/>.</summary>
public sealed class InMemoryParentWorkSource : ISubagentParentWorkSource
{
    private readonly ConcurrentDictionary<string, SubagentParentWork> _parents = new(StringComparer.Ordinal);

    /// <summary>Sets the work of a parent session.</summary>
    public void Set(string parentId, SubagentParentWork work) => _parents[parentId] = work;

    /// <inheritdoc />
    public ValueTask<SubagentParentWork?> FindAsync(string parentId, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_parents.GetValueOrDefault(parentId));
}
