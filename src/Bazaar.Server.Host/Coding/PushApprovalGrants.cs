using Bazaar.Approvals;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Security.Approvals;
using Npgsql;

namespace Bazaar.Server.Host.Coding;

/// <summary>
/// Host lookup and single-use consumption of the owner approval for one push (doc 17, "Approval grant lookup"). Every failure is
/// "no grant" or "not consumed"; nothing here throws for a store problem.
/// </summary>
public interface IPushApprovalGrants
{
    /// <summary>Finds the owner-signed, unexpired, unconsumed approval for exactly this push in this session, or null.</summary>
    ValueTask<ToolApprovalGrant?> FindAsync(PushGateRequest request, string sessionId, CancellationToken cancellationToken);

    /// <summary>Spends the grant. True for exactly one caller per grant; false when it was already spent, expired or the store failed.</summary>
    ValueTask<bool> ConsumeAsync(ToolApprovalGrant grant, CancellationToken cancellationToken);
}

/// <summary>The grants source used when no approval store is bound: no grant ever exists, so a hard-tainted push stays at "needs approval".</summary>
public sealed class NoPushApprovalGrants : IPushApprovalGrants
{
    /// <summary>The shared instance.</summary>
    public static NoPushApprovalGrants Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask<ToolApprovalGrant?> FindAsync(PushGateRequest request, string sessionId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ToolApprovalGrant?>(null);

    /// <inheritdoc />
    public ValueTask<bool> ConsumeAsync(ToolApprovalGrant grant, CancellationToken cancellationToken) => ValueTask.FromResult(false);
}

/// <summary>Lists the ids of approved, unexpired approval rows for one agent, session, tool and dispatch hash. Candidates only; the caller re-checks each row.</summary>
public interface IApprovedPushApprovalIds
{
    /// <summary>The candidate ids, earliest expiry first. May throw on a store failure.</summary>
    ValueTask<IReadOnlyList<Guid>> FindIdsAsync(string agentId, Guid sessionId, string tool, byte[] argsHash, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>One read-only query over the <c>approval</c> table (migration 0011). Server's <c>IApprovalStore</c> has no query method and is left as it is.</summary>
public sealed class PostgresApprovedPushApprovalIds : IApprovedPushApprovalIds
{
    private const int MaxCandidates = 8;

    private readonly NpgsqlDataSource _dataSource;

    /// <summary>Creates the source.</summary>
    public PostgresApprovedPushApprovalIds(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<Guid>> FindIdsAsync(
        string agentId, Guid sessionId, string tool, byte[] argsHash, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var utc = now.ToUniversalTime();
        await using var cmd = _dataSource.CreateCommand(
            "SELECT id FROM approval WHERE status = 'approved' AND agent_id = $1 AND session_id = $2 AND tool = $3 " +
            "AND args_hash = $4 AND expires_at > $5 ORDER BY expires_at, id LIMIT " + MaxCandidates);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = agentId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = sessionId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = tool });
        cmd.Parameters.Add(new NpgsqlParameter<byte[]> { TypedValue = argsHash });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero) });
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetFieldValue<Guid>(0));
        }

        return ids;
    }
}

/// <summary>
/// <see cref="IPushApprovalGrants"/> over Server's approval store. A row is a grant only when it is approved, unconsumed, unexpired,
/// bound to this agent, session, tool, branch and dispatch hash, and carries an owner signature that verifies against a registered,
/// unrevoked owner device key. Consumption is one compare-and-set from Approved to Consumed.
/// </summary>
public sealed class ApprovalStorePushGrants : IPushApprovalGrants
{
    private readonly IApprovedPushApprovalIds _candidates;
    private readonly IApprovalStore _store;
    private readonly IOwnerKeyRegistry _keys;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    /// <summary>Creates the source.</summary>
    public ApprovalStorePushGrants(IApprovedPushApprovalIds candidates, IApprovalStore store, IOwnerKeyRegistry keys, TimeProvider time, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _candidates = candidates;
        _store = store;
        _keys = keys;
        _time = time;
        _timeout = timeout;
    }

    /// <inheritdoc />
    public async ValueTask<ToolApprovalGrant?> FindAsync(PushGateRequest request, string sessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(sessionId, "D", out var session) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            return null;
        }

        try
        {
            var hash = PermissionGatePushGate.DispatchHashOf(request);
            var now = _time.GetUtcNow();
            return await FindCoreAsync(request, session, hash, now, cancellationToken).AsTask().WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> ConsumeAsync(ToolApprovalGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            return await ConsumeCoreAsync(grant, cancellationToken).AsTask().WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<ToolApprovalGrant?> FindCoreAsync(PushGateRequest request, Guid session, byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await _candidates.FindIdsAsync(request.AgentId, session, PermissionGatePushGate.ToolName, hash, now, ct).ConfigureAwait(false);
        ApprovalRequest? best = null;
        foreach (var id in ids.Distinct())
        {
            var row = await _store.GetAsync(id, ct).ConfigureAwait(false);
            if (row is null || row.Id != id || !await IsOwnerGrantAsync(row, request, session, hash, now, ct).ConfigureAwait(false))
            {
                continue;
            }

            if (best is null || row.ExpiresAt < best.ExpiresAt || (row.ExpiresAt == best.ExpiresAt && row.Id.CompareTo(best.Id) < 0))
            {
                best = row;
            }
        }

        return best is null ? null : new ToolApprovalGrant(best.Id, best.ArgsHash, best.ExpiresAt);
    }

    private async ValueTask<bool> IsOwnerGrantAsync(ApprovalRequest row, PushGateRequest request, Guid session, byte[] hash, DateTimeOffset now, CancellationToken ct)
    {
        if (row.Status != ApprovalStatus.Approved
            || row.ConsumedAt is not null
            || row.ExpiresAt <= now
            || !string.Equals(row.AgentId, request.AgentId, StringComparison.Ordinal)
            || row.SessionId != session
            || !string.Equals(row.Tool, PermissionGatePushGate.ToolName, StringComparison.Ordinal)
            || !string.Equals(row.Target, request.Branch.Value, StringComparison.Ordinal)
            || !row.ArgsHash.AsSpan().SequenceEqual(hash))
        {
            return false;
        }

        // Only the owner's signed decision makes a grant. A row an agent wrote has no valid signature from a registered device.
        if (string.IsNullOrWhiteSpace(row.DecidedBy) || row.DecidedAt is null || row.Signature is not { Length: ApprovalSignature.SignatureLength } signature
            || !ApprovalSignature.IsValidDeviceId(row.DeviceId))
        {
            return false;
        }

        var key = await _keys.FindAsync(row.DeviceId!, ct).ConfigureAwait(false);
        if (key is null || key.Revoked || !string.Equals(key.OwnerId, row.DecidedBy, StringComparison.Ordinal) || !string.Equals(key.DeviceId, row.DeviceId, StringComparison.Ordinal))
        {
            return false;
        }

        var message = ApprovalSignature.Message(row.Id, ApprovalDecision.Approve, row.ArgsHash, row.ExpiresAt, row.DeviceId!);
        return ApprovalSignature.Verify(key.PublicKeySpki, message, signature);
    }

    private async ValueTask<bool> ConsumeCoreAsync(ToolApprovalGrant grant, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var row = await _store.GetAsync(grant.ApprovalId, ct).ConfigureAwait(false);
        if (row is null
            || row.Status != ApprovalStatus.Approved
            || row.ConsumedAt is not null
            || row.ExpiresAt <= now
            || grant.ExpiresAt <= now
            || !row.ArgsHash.AsSpan().SequenceEqual(grant.ArgsHash))
        {
            return false;
        }

        // The store's compare-and-set lets exactly one caller move Approved to Consumed.
        return await _store.TryReplaceAsync(row.Id, ApprovalStatus.Approved, row with { Status = ApprovalStatus.Consumed, ConsumedAt = now }, ct).ConfigureAwait(false);
    }
}
