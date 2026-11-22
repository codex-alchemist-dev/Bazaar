using System.Collections.Concurrent;
using Bazaar.Server.PsychWard.Abstractions;

namespace Bazaar.Server.PsychWard;

/// <summary>In-memory <see cref="IVentStore"/>, keyed by failure reference.</summary>
public sealed class InMemoryVentStore : IVentStore
{
    private readonly ConcurrentDictionary<string, VentDraft> _vents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> _openFailures = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<VentDraft> GetOrAddAsync(VentDraft candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return Task.FromResult(_vents.GetOrAdd(candidate.FailureRef, candidate));
    }

    /// <inheritdoc />
    public Task<VentDraft?> GetAsync(string failureRef, CancellationToken cancellationToken) =>
        Task.FromResult(_vents.GetValueOrDefault(failureRef));

    /// <inheritdoc />
    public Task UpdateAsync(VentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _vents[draft.FailureRef] = draft;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VentDraft>> ListByStatusAsync(VentStatus status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VentDraft>>([.. _vents.Values.Where(v => v.Status == status).OrderBy(v => v.CreatedAt)]);

    /// <inheritdoc />
    public Task AddOpenFailureAsync(Guid agentId, string failureRef, CancellationToken cancellationToken)
    {
        if (!_vents.ContainsKey(failureRef))
        {
            _openFailures.TryAdd(failureRef, agentId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResolveFailureAsync(string failureRef, CancellationToken cancellationToken)
    {
        _openFailures.TryRemove(failureRef, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> HasOpenFailureAsync(Guid agentId, CancellationToken cancellationToken) =>
        Task.FromResult(_openFailures.Values.Contains(agentId));
}
