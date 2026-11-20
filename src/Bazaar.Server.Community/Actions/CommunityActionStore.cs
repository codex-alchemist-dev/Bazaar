using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.Security.Audit;
using Npgsql;

namespace Bazaar.Server.Community.Actions;

/// <summary>Statements for <c>community_action</c> (migration community/0003). Constants, so no statement is built from input.</summary>
public static class CommunityActionSqlText
{
    /// <summary>Inserts one row and returns its id. Parameter names match the columns; taint_sources is cast from text.</summary>
    public const string Insert = """
        INSERT INTO community_action
            (agent_id, sponsor_id, platform, action, target_url, body, body_sha256, approval_id, taint_sources, external_id, status)
        VALUES
            (@agent_id, @sponsor_id, @platform, @action, @target_url, @body, @body_sha256, @approval_id, CAST(@taint_sources AS jsonb), @external_id, @status)
        RETURNING id
        """;

    /// <summary>Newest rows first for one agent on one platform. Served by the (agent_id, platform, created_at) index.</summary>
    public const string ListForAgent = """
        SELECT id, agent_id, sponsor_id, platform, action, target_url, body, body_sha256, approval_id,
               taint_sources::text, external_id, status, created_at
        FROM community_action
        WHERE agent_id = @agent_id AND platform = @platform
        ORDER BY created_at DESC, id DESC
        LIMIT @limit
        """;
}

/// <summary>The database calls <see cref="CommunityActionStore"/> needs. <see cref="NpgsqlCommunityActionSql"/> is the production implementation.</summary>
public interface ICommunityActionSql
{
    /// <summary>Inserts one row and returns its identity value.</summary>
    Task<long> InsertAsync(CommunityActionRecord record, CancellationToken ct);

    /// <summary>Returns up to <paramref name="limit"/> rows for the agent and platform, newest first.</summary>
    Task<IReadOnlyList<CommunityActionRecord>> ListForAgentAsync(string agentId, string platform, int limit, CancellationToken ct);
}

/// <summary>Raw Npgsql over the shared <see cref="NpgsqlDataSource"/>. Every value is a bound parameter.</summary>
public sealed class NpgsqlCommunityActionSql(NpgsqlDataSource dataSource) : ICommunityActionSql
{
    /// <inheritdoc />
    public async Task<long> InsertAsync(CommunityActionRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(CommunityActionSqlText.Insert, conn);
        cmd.Parameters.AddWithValue("agent_id", record.AgentId);
        cmd.Parameters.AddWithValue("sponsor_id", record.SponsorId);
        cmd.Parameters.AddWithValue("platform", record.Platform);
        cmd.Parameters.AddWithValue("action", record.Action.ToString());
        cmd.Parameters.AddWithValue("target_url", OrNull(record.TargetUrl));
        cmd.Parameters.AddWithValue("body", OrNull(record.Body));
        cmd.Parameters.AddWithValue("body_sha256", record.BodySha256 is null ? DBNull.Value : record.BodySha256);
        cmd.Parameters.AddWithValue("approval_id", OrNull(record.ApprovalId));
        cmd.Parameters.AddWithValue("taint_sources", record.TaintSourcesJson);
        cmd.Parameters.AddWithValue("external_id", OrNull(record.ExternalId));
        cmd.Parameters.AddWithValue("status", record.Status);

        var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return (long)id!;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CommunityActionRecord>> ListForAgentAsync(string agentId, string platform, int limit, CancellationToken ct)
    {
        var rows = new List<CommunityActionRecord>();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(CommunityActionSqlText.ListForAgent, conn);
        cmd.Parameters.AddWithValue("agent_id", agentId);
        cmd.Parameters.AddWithValue("platform", platform);
        cmd.Parameters.AddWithValue("limit", limit);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Map(reader));
        return rows;
    }

    private static object OrNull(string? value) => value is null ? DBNull.Value : value;

    private static CommunityActionRecord Map(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        Enum.Parse<CommunityAction>(r.GetString(4)),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.IsDBNull(7) ? null : r.GetFieldValue<byte[]>(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetString(9),
        r.IsDBNull(10) ? null : r.GetString(10),
        r.GetString(11),
        r.GetFieldValue<DateTimeOffset>(12));
}

/// <summary>
/// Append-only store for outward community actions (doc 36, "Rate limits, audit and takedown"). Each append writes one
/// <c>community_action</c> row and one audit entry (kind <see cref="AuditKind"/>, actor = agent id). A withdrawal is an
/// append too. Nothing is updated or deleted.
/// </summary>
/// <remarks>
/// The row and the audit entry go through two connections: <see cref="IAuditLog"/> opens its own transaction. The row is
/// written first, because the audit payload carries its id. If the audit write fails, the call throws after the row is
/// committed. The caller must treat the action as unconfirmed and must not repeat the outward action. Auditing the failure
/// is the caller's job.
/// </remarks>
public sealed class CommunityActionStore(ICommunityActionSql sql, IAuditLog audit, TimeProvider? clock = null)
{
    /// <summary>Audit kind for every community action entry.</summary>
    public const string AuditKind = "community.action";

    /// <summary>Largest page <see cref="ListForAgentAsync"/> returns.</summary>
    public const int MaxListLimit = 500;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Appends one action row and one audit entry. The body hash is computed from <see cref="CommunityActionRecord.Body"/>
    /// when <see cref="CommunityActionRecord.BodySha256"/> is null, and checked against it when both are given.
    /// </summary>
    /// <returns>The new row id.</returns>
    public async Task<long> AppendAsync(CommunityActionRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.AgentId, nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SponsorId, nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Platform, nameof(record));
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Status, nameof(record));
        if (!Enum.IsDefined(record.Action))
            throw new ArgumentException("Unknown community action.", nameof(record));

        var bodySha256 = ResolveBodyHash(record);
        var id = await sql.InsertAsync(record with { BodySha256 = bodySha256 }, ct).ConfigureAwait(false);

        var payload = AuditPayload(id, record.Action, record.Platform, bodySha256);
        await audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), record.AgentId, AuditKind, payload), ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Records a withdrawal of an earlier own post as a new row (action <see cref="CommunityAction.WithdrawOwn"/>). The original
    /// item's external id goes in <see cref="CommunityActionRecord.TargetUrl"/>. The earlier row is not touched.
    /// </summary>
    /// <param name="status">Outcome of the platform call that withdrew the item. Defaults to "done".</param>
    /// <returns>The new row id.</returns>
    public async Task<long> WithdrawAsync(
        string agentId,
        string sponsorId,
        string platform,
        string externalId,
        string status = "done",
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId, nameof(externalId));
        var record = new CommunityActionRecord(
            Id: 0,
            AgentId: agentId,
            SponsorId: sponsorId,
            Platform: platform,
            Action: CommunityAction.WithdrawOwn,
            TargetUrl: externalId,
            Body: null,
            BodySha256: null,
            ApprovalId: null,
            TaintSourcesJson: "[]",
            ExternalId: null,
            Status: status,
            CreatedAt: default);
        return await AppendAsync(record, ct).ConfigureAwait(false);
    }

    /// <summary>Returns up to <paramref name="limit"/> rows for one agent on one platform, newest first.</summary>
    public async Task<IReadOnlyList<CommunityActionRecord>> ListForAgentAsync(string agentId, string platform, int limit, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId, nameof(agentId));
        ArgumentException.ThrowIfNullOrWhiteSpace(platform, nameof(platform));
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxListLimit);
        return await sql.ListForAgentAsync(agentId, platform, limit, ct).ConfigureAwait(false);
    }

    private static byte[]? ResolveBodyHash(CommunityActionRecord record)
    {
        if (record.Body is null)
            return record.BodySha256;

        var computed = SHA256.HashData(Encoding.UTF8.GetBytes(record.Body));
        if (record.BodySha256 is null)
            return computed;
        if (!computed.AsSpan().SequenceEqual(record.BodySha256))
            throw new ArgumentException("BodySha256 does not match Body.", nameof(record));
        return computed;
    }

    /// <summary>Audit payload: row id, action, platform and body hash. Never the body text.</summary>
    private static string AuditPayload(long id, CommunityAction action, string platform, byte[]? bodySha256)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("action", action.ToString());
            writer.WriteString("platform", platform);
            if (bodySha256 is null)
                writer.WriteNull("body_sha256");
            else
                writer.WriteString("body_sha256", Convert.ToHexStringLower(bodySha256));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
