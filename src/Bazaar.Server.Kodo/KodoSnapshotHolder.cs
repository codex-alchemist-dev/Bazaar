using Bazaar.Contracts.Kodo;

namespace Bazaar.Server.Kodo;

/// <summary>
/// Holds the latest snapshot in one reference, swapped with compare-and-exchange, so readers never block and never see a torn
/// value. It does no evaluation: the evaluator host feeds it (doc 46 section 15.7).
/// </summary>
public sealed class KodoSnapshotHolder : IKodoSnapshotProvider, IKodoSnapshotPublisher
{
    private KodoStatusSnapshotDto? _current;

    public KodoStatusSnapshotDto? Current => Volatile.Read(ref _current);

    public bool Publish(KodoStatusSnapshotDto snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        while (true)
        {
            KodoStatusSnapshotDto? seen = Volatile.Read(ref _current);
            if (seen is not null && IsOlder(snapshot, seen))
            {
                return false;
            }

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, snapshot, seen), seen))
            {
                return true;
            }
        }
    }

    private static bool IsOlder(KodoStatusSnapshotDto candidate, KodoStatusSnapshotDto held) =>
        candidate.Revision < held.Revision
        || (candidate.Revision == held.Revision && candidate.Status.AsOf <= held.Status.AsOf);
}

/// <summary>The shipped incident source: none open, until the incident module (doc 46 section 16.3) registers its own.</summary>
public sealed class NoKodoIncidents : IKodoIncidentSource
{
    public Task<IReadOnlyList<KodoIncidentSummary>> ListOpenAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<KodoIncidentSummary>>([]);
}
