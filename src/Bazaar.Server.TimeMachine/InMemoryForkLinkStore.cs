namespace Bazaar.Server.TimeMachine;

/// <summary>An in-memory <see cref="IForkLinkStore"/> for tests and hosts without Postgres. Not durable.</summary>
public sealed class InMemoryForkLinkStore : IForkLinkStore
{
    private readonly Lock _gate = new();
    private readonly List<ForkLink> _links = [];

    /// <inheritdoc />
    public Task<ForkLink?> FindAsync(Guid sourceGcId, long sourceSeq, Guid requestId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_links.FirstOrDefault(l => Matches(l, sourceGcId, sourceSeq, requestId)));
    }

    /// <inheritdoc />
    public Task<(ForkLink Link, bool Created)> AppendAsync(ForkLink link, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_gate)
        {
            var existing = _links.FirstOrDefault(l => Matches(l, link.SourceGcId, link.SourceSeq, link.RequestId));
            if (existing is not null)
                return Task.FromResult((existing, false));
            _links.Add(link);
            return Task.FromResult((link, true));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ForkLink>> ListBySourceAsync(Guid sourceGcId, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ForkLink>>([.. _links.Where(l => l.SourceGcId == sourceGcId).OrderBy(l => l.CreatedAt)]);
    }

    private static bool Matches(ForkLink l, Guid sourceGcId, long sourceSeq, Guid requestId) =>
        l.SourceGcId == sourceGcId && l.SourceSeq == sourceSeq && l.RequestId == requestId;
}
