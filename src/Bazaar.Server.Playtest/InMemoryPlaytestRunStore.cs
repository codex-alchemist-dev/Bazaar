using System.Collections.Concurrent;
using Bazaar.Server.Playtest.Abstractions;

namespace Bazaar.Server.Playtest;

/// <summary>Process-local run store: the default until the host supplies a data source, and the store tests use.</summary>
public sealed class InMemoryPlaytestRunStore : IPlaytestRunStore
{
    private readonly ConcurrentDictionary<Guid, PlaytestResult> _runs = new();

    public Task SaveAsync(PlaytestResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        _runs[result.RunId] = result;
        return Task.CompletedTask;
    }

    public Task<PlaytestResult?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult(_runs.TryGetValue(runId, out var result) ? result : null);
}
