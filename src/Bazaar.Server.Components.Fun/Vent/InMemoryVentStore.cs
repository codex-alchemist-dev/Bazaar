using System.Collections.Concurrent;

namespace Bazaar.Server.Fun.Vent;

/// <summary>An in-memory <see cref="IVentStore"/> for tests and the fake runtime. Keyed by failure reference.</summary>
public sealed class InMemoryVentStore : IVentStore
{
    private readonly ConcurrentDictionary<string, VentDraft> _drafts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public VentDraft GetOrAdd(VentDraft candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return _drafts.GetOrAdd(candidate.FailureRef, candidate);
    }
}
