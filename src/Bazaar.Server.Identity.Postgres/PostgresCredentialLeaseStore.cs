using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Npgsql;

namespace Bazaar.Server.Identity.Postgres;

/// <summary>
/// Durable <see cref="ICredentialLeaseStore"/> over <c>credential_lease</c> (common 0019, identity 0001). An approval id is unique in the
/// table, so replay is refused across restarts. Expiry and the revocation scope are checked in SQL against the caller's clock.
/// </summary>
/// <remarks>
/// Agent ids are GUIDs in this table, so a non-GUID agent id is refused. Issue relies on the unique partial index: a second lease for
/// one approval inserts nothing and returns null, the same answer the in-memory store gives.
/// </remarks>
public sealed class PostgresCredentialLeaseStore(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null) : ICredentialLeaseStore
{
    private const string _issue =
        "INSERT INTO credential_lease (lease_id, agent_id, credential_id, scope, issued_at, expires_at, approval_id) " +
        "VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT (approval_id) WHERE approval_id IS NOT NULL DO NOTHING RETURNING lease_id";
    private const string _columns = "lease_id, agent_id, credential_id, scope, issued_at, expires_at, revoked_at, approval_id";
    private const string _get =
        "SELECT " + _columns + " FROM credential_lease WHERE lease_id = $1 AND agent_id = $2 AND revoked_at IS NULL AND expires_at > $3";
    private const string _listActive =
        "SELECT " + _columns + " FROM credential_lease WHERE agent_id = $1 AND revoked_at IS NULL AND expires_at > $2 ORDER BY issued_at";
    private const string _approvalUsed = "SELECT EXISTS (SELECT 1 FROM credential_lease WHERE approval_id = $1)";
    private const string _revokeAgent = "UPDATE credential_lease SET revoked_at = $1 WHERE agent_id = $2 AND revoked_at IS NULL";
    private const string _revokeAll = "UPDATE credential_lease SET revoked_at = $1 WHERE revoked_at IS NULL";

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<CredentialLease?> IssueAsync(
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
        var agent = ParseAgent(agentId);
        var now = Micros(_time.GetUtcNow());
        if (expiresAt <= now)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A lease must expire after it is issued.");
        }

        await using var cmd = dataSource.CreateCommand(_issue);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id.Value });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agent });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = credentialId.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = scope });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(expiresAt) });
        cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = approvalId });
        var inserted = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (inserted is null)
        {
            return null;
        }

        return new CredentialLease(id, agentId, credentialId, scope, now, Micros(expiresAt), null, approvalId);
    }

    /// <inheritdoc />
    public async Task<CredentialLease?> GetAsync(LeaseId id, string agentId, CancellationToken ct = default)
    {
        var agent = ParseAgent(agentId);
        await using var cmd = dataSource.CreateCommand(_get);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id.Value });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agent });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(_time.GetUtcNow()) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CredentialLease>> ListActiveAsync(string agentId, CancellationToken ct = default)
    {
        var agent = ParseAgent(agentId);
        await using var cmd = dataSource.CreateCommand(_listActive);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agent });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = Micros(_time.GetUtcNow()) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var leases = new List<CredentialLease>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            leases.Add(Read(reader));
        }

        return leases;
    }

    /// <inheritdoc />
    public async Task<bool> IsApprovalUsedAsync(Guid approvalId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand(_approvalUsed);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = approvalId });
        return (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The scope is not one this store knows.</exception>
    public async Task RevokeAsync(RevocationScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var now = Micros(_time.GetUtcNow());
        await using var cmd = dataSource.CreateCommand(scope switch
        {
            AgentRevocation => _revokeAgent,
            GlobalRevocation => _revokeAll,
            _ => throw new ArgumentException("Unknown revocation scope.", nameof(scope)),
        });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        if (scope is AgentRevocation agent)
        {
            cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ParseAgent(agent.AgentId) });
        }

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task RevokeAgentAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return RevokeAsync(new AgentRevocation(agentId), ct);
    }

    private static Guid ParseAgent(string agentId) =>
        Guid.TryParse(agentId, out var agent)
            ? agent
            : throw new ArgumentException("The agent id must be a GUID for the durable lease store.", nameof(agentId));

    private static CredentialLease Read(NpgsqlDataReader reader) =>
        new(
            new LeaseId(reader.GetGuid(0)),
            reader.GetGuid(1).ToString(),
            new CredentialId(reader.GetGuid(2)),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7));

    /// <summary>Cuts an instant to whole microseconds, the precision Postgres stores, so a write and its read-back agree.</summary>
    private static DateTimeOffset Micros(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
