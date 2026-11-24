using System.Collections.Concurrent;

namespace Bazaar.Server.WhatIf;

/// <summary>An in-memory run store for tests and installs without a database.</summary>
public sealed class InMemoryWhatIfRunStore : IWhatIfRunStore
{
    private readonly ConcurrentDictionary<Guid, WhatIfRun> _runs = new();

    public Task SaveAsync(WhatIfRun run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<WhatIfRun?> GetAsync(Guid runId, CancellationToken ct = default) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run) ? run : null);
}
