using Bazaar.Server.Agents.Data;
using Bazaar.Server.Setup.FirstRun.Gateway;
using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>
/// The seeded rows (doc 45 section 1.15). The GC is a <c>general</c> GC with a fixed id; the agent comes from the Agents
/// first-run seams (held, marker <see cref="FirstRunMarkers.DefaultAgent"/>), then joins the GC, then gets its model policy.
/// "Provisioned" is the agent in Created with its hold set; activating releases the hold and starts onboarding, and the agent
/// runtime finishes the move to active. The GC log and <c>gc_member</c> are append-only, so a rollback archives the GC and
/// leaves the membership row (doc 01).
/// </summary>
public sealed class PostgresFirstRunStore(
    NpgsqlDataSource dataSource,
    IAgentFirstRunSeeds agents,
    DefaultAgentPolicyGrant policyGrant) : IFirstRunStore
{
    /// <inheritdoc />
    public async Task<SeededGc> EnsureGeneralGcAsync(string title, Guid ownerPrincipalId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var id = FirstRunMarkers.GeneralGcId;

        await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO gc (gc_id, type_key, title, created_by) VALUES ($1, 'general', $2, $3) ON CONFLICT (gc_id) DO NOTHING", conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = id });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = title.Trim() });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerPrincipalId });
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new SeededGc(id, false);
            }
        }

        await AddMemberAsync(conn, tx, id, ownerPrincipalId, "owner", cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SeededGc(id, true);
    }

    /// <inheritdoc />
    public async Task<SeededAgent> EnsureDefaultAgentAsync(string name, string purpose, Guid? gcId, IReadOnlyList<string> whitelist, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(whitelist);

        // A stored agent keeps its own policy and membership; only a new agent is given them. The purpose is the Soul's business and
        // no Soul ships with the seed, so it is not stored here.
        if (await agents.FindByMarkerAsync(FirstRunMarkers.DefaultAgent, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return new SeededAgent(existing, false);
        }

        var agentId = await agents.CreateHeldAsync(name.Trim(), FirstRunMarkers.DefaultAgent, ct: cancellationToken).ConfigureAwait(false);
        if (gcId is { } gc)
        {
            await using var conn = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await AddMemberAsync(conn, null, gc, agentId, "member", cancellationToken).ConfigureAwait(false);
        }

        await policyGrant.GrantAsync(agentId, whitelist, cancellationToken).ConfigureAwait(false);
        return new SeededAgent(agentId, true);
    }

    /// <inheritdoc />
    public async Task ActivateAgentAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        await agents.ReleaseHoldAsync(agentId, cancellationToken).ConfigureAwait(false);
        await agents.TryStartAutomaticOnboardingAsync(agentId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAgentAsync(Guid agentId, CancellationToken cancellationToken = default) =>
        await agents.RemoveUnstartedAsync(agentId, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task ArchiveGcAsync(Guid gcId, CancellationToken cancellationToken = default)
    {
        await using var cmd = dataSource.CreateCommand("UPDATE gc SET archived_at = now() WHERE gc_id = $1 AND archived_at IS NULL");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // gc_member is append-only and its key includes joined_seq, so a repeat is skipped by an existence check, never updated.
    private static async Task AddMemberAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, Guid gcId, Guid principalId, string role, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) SELECT $1, $2, $3, 0 WHERE NOT EXISTS (SELECT 1 FROM gc_member WHERE gc_id = $1 AND principal_id = $2 AND left_seq IS NULL)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = gcId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = principalId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = role });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
