using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Bazaar.Secrets;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Secrets.Postgres;

/// <summary>
/// Issues credentials into <c>secret_agent_credential</c> (migration secrets/0002). The agent's state is read with a shared row lock
/// (FOR SHARE), so a concurrent quarantine or revoke, which takes FOR UPDATE, waits for the issue to commit or fail. Refusals are
/// audited in the same transaction before they throw.
/// </summary>
public sealed class PostgresAgentCredentialIssuer(NpgsqlDataSource dataSource, ITransactionalAuditLog audit, TimeProvider time) : IAgentCredentialIssuer
{
    private static readonly TimeSpan _minLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _maxLifetime = TimeSpan.FromHours(24);
    public async Task<AgentCredential> IssueAsync(AgentId agent, SecretScope scope, TimeSpan lifetime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(scope);
        if (lifetime < _minLifetime || lifetime > _maxLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Credential lifetime must be between one minute and 24 hours.");
        }

        var now = time.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string? state;
        await using (var select = new NpgsqlCommand("SELECT state FROM secret_agent WHERE agent_id = $1 FOR SHARE", conn, tx))
        {
            select.Parameters.AddWithValue(agent.Value);
            state = await select.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }

        if (state is null)
        {
            throw new AgentCredentialDeniedException($"Agent '{agent.Value}' is not registered.");
        }

        if (state != nameof(AgentState.Active))
        {
            await AuditAsync(conn, tx, now, agent, "secrets.credential.denied", scope.Key, reason: $"agent is {state}", issuanceId: null, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            throw new AgentCredentialDeniedException($"Agent '{agent.Value}' is {state}; no credential is issued.");
        }

        var tokenBytes = RandomNumberGenerator.GetBytes(32);
        var issuanceId = Guid.NewGuid().ToString("N");
        var expiresAt = now + lifetime;
        var tokenHash = SHA256.HashData(tokenBytes);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO secret_agent_credential (issuance_id, agent_id, token_hash, scope, issued_at, expires_at) VALUES ($1, $2, $3, $4, $5, $6)", conn, tx))
        {
            insert.Parameters.AddWithValue(issuanceId);
            insert.Parameters.AddWithValue(agent.Value);
            insert.Parameters.AddWithValue(tokenHash);
            insert.Parameters.AddWithValue(scope.Key);
            insert.Parameters.AddWithValue(now);
            insert.Parameters.AddWithValue(expiresAt);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await AuditAsync(conn, tx, now, agent, "secrets.credential.issued", scope.Key, reason: null, issuanceId, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        var token = new SecretValue(tokenBytes);
        CryptographicOperations.ZeroMemory(tokenBytes);
        return new AgentCredential(issuanceId, token, scope, expiresAt);
    }

    public async Task RevokeAsync(AgentId agent, string issuanceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuanceId);
        var now = time.GetUtcNow();
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int changed;
        await using (var update = new NpgsqlCommand(
            "UPDATE secret_agent_credential SET revoked_at = $3 WHERE issuance_id = $1 AND agent_id = $2 AND revoked_at IS NULL", conn, tx))
        {
            update.Parameters.AddWithValue(issuanceId);
            update.Parameters.AddWithValue(agent.Value);
            update.Parameters.AddWithValue(now);
            changed = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (changed == 1)
        {
            await AuditAsync(conn, tx, now, agent, "secrets.credential.revoked", scope: null, reason: null, issuanceId, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task AuditAsync(NpgsqlConnection conn, NpgsqlTransaction tx, DateTimeOffset at, AgentId agent, string kind,
        string? scope, string? reason, string? issuanceId, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["agent"] = agent.Value,
            ["scope"] = scope,
            ["reason"] = reason,
            ["issuance"] = issuanceId,
        };
        await audit.AppendInTransactionAsync(conn, tx, new AuditEntry(at, "host:secret-store", kind, payload.ToJsonString()), ct).ConfigureAwait(false);
    }
}
