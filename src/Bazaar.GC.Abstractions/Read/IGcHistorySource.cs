using Bazaar.Contracts.Wire;

namespace Bazaar.Server.Gc.Read;

/// <summary>Reads older messages of a GC for the history request (doc 02). The caller has already checked the viewer's grant.</summary>
public interface IGcHistorySource
{
    /// <summary>One page of messages older than <paramref name="before"/>, newest first, at most <paramref name="limit"/>.</summary>
    ValueTask<GcPage> ReadHistoryAsync(Guid gcId, long before, int limit, CancellationToken ct);
}
