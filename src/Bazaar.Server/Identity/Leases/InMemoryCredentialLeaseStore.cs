using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Thread-safe in-memory <see cref="ICredentialLeaseStore"/>, for tests and single-process hosts. State is lost on restart;
/// the durable store is the Postgres implementation (doc 14 work package 1).
/// </summary>
public sealed class InMemoryCredentialLeaseStore(TimeProvider time) : ICredentialLeaseStore
{
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly Lock _gate = new();
    private readonly Dictionary<LeaseId, CredentialLease> _leases = [];
    private readonly HashSet<Guid> _usedApprovals = [];

    /// <inheritdoc />
    public Task<CredentialLease?> IssueAsync(
        LeaseId id,
        string agentId,
        CredentialId credentialId,
        string scope,
        DateTimeOffset expiresAt,
        Guid? approvalId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        var now = _time.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A lease must expire after it is issued.");
        }

        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (approvalId is { } approval && _usedApprovals.Contains(approval))
            {
                return Task.FromResult<CredentialLease?>(null);
            }

            if (!_leases.TryAdd(id, new CredentialLease(id, agentId, credentialId, scope, now, expiresAt, null, approvalId)))
            {
                throw new InvalidOperationException("A lease with this id already exists.");
            }

            if (approvalId is { } used)
            {
                _usedApprovals.Add(used);
            }

            return Task.FromResult<CredentialLease?>(_leases[id]);
        }
    }

    /// <inheritdoc />
    public Task<CredentialLease?> GetAsync(LeaseId id, string agentId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            if (_leases.TryGetValue(id, out var lease)
                && lease.AgentId == agentId
                && lease.IsActive(now))
            {
                return Task.FromResult<CredentialLease?>(lease);
            }

            return Task.FromResult<CredentialLease?>(null);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CredentialLease>> ListActiveAsync(string agentId, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<CredentialLease> active = _leases.Values
                .Where(lease => lease.AgentId == agentId && lease.IsActive(now))
                .OrderBy(lease => lease.IssuedAt)
                .ToList();
            return Task.FromResult(active);
        }
    }

    /// <inheritdoc />
    public Task<bool> IsApprovalUsedAsync(Guid approvalId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_usedApprovals.Contains(approvalId));
        }
    }

    /// <inheritdoc />
    public Task RevokeAsync(RevocationScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (key, lease) in _leases.ToList())
            {
                if (lease.RevokedAt is not null)
                {
                    continue;
                }

                var inScope = scope switch
                {
                    AgentRevocation agent => lease.AgentId == agent.AgentId,
                    GlobalRevocation => true,
                    _ => throw new ArgumentException("Unknown revocation scope.", nameof(scope)),
                };

                if (inScope)
                {
                    _leases[key] = lease with { RevokedAt = now };
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc />
    public Task RevokeAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return RevokeAsync(new AgentRevocation(agentId), ct);
    }
}
