using Bazaar.Server.Community.Actions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Community.Observatory;

/// <summary>Statements for the Observatory community log. Constants, so no statement is built from input.</summary>
public static class CommunityLogSqlText
{
    /// <summary>
    /// Reads log rows, newest first. The select list is the <see cref="CommunityLogRow"/> columns only, so the body text is
    /// never read. Optional filters are bound parameters: a null value disables that filter.
    /// </summary>
    public const string Select = """
        SELECT id, agent_id, platform, action, target_url, body_sha256, status, created_at, approval_id, external_id
        FROM community_action
        WHERE (@agent_id::text IS NULL OR agent_id = @agent_id::text)
          AND (@platform::text IS NULL OR platform = @platform::text)
          AND (@after_created_at::timestamptz IS NULL OR (created_at, id) < (@after_created_at::timestamptz, @after_id::bigint))
        ORDER BY created_at DESC, id DESC
        LIMIT @limit
        """;
}

/// <summary>Options for <see cref="NpgsqlCommunityLogReader.ListAsync"/>. <see cref="Limit"/> is clamped to 1 to 200.</summary>
public sealed record CommunityLogFilter
{
    /// <summary>Smallest page size.</summary>
    public const int MinLimit = 1;

    /// <summary>Largest page size.</summary>
    public const int MaxLimit = 200;

    /// <summary>Page size used when the caller gives none.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Creates a filter. A null <paramref name="AgentId"/> or <paramref name="Platform"/> matches all values.</summary>
    /// <param name="AgentId">Only rows for this agent, or null for all agents.</param>
    /// <param name="Platform">Only rows for this platform, or null for all platforms.</param>
    /// <param name="Limit">Page size. Values outside 1 to 200 are clamped.</param>
    /// <param name="After">Position of the last row of the previous page, or null for the first page.</param>
    public CommunityLogFilter(string? AgentId = null, string? Platform = null, int Limit = DefaultLimit, CommunityLogPosition? After = null)
    {
        this.AgentId = AgentId;
        this.Platform = Platform;
        this.Limit = Math.Clamp(Limit, MinLimit, MaxLimit);
        this.After = After;
    }

    /// <summary>Only rows strictly older than this position in (created_at, id) order, or null for the newest rows.</summary>
    public CommunityLogPosition? After { get; }

    /// <summary>Only rows for this agent, or null for all agents.</summary>
    public string? AgentId { get; }

    /// <summary>Only rows for this platform, or null for all platforms.</summary>
    public string? Platform { get; }

    /// <summary>Page size, always between <see cref="MinLimit"/> and <see cref="MaxLimit"/>.</summary>
    public int Limit { get; }
}

/// <summary>Projects <see cref="CommunityActionRecord"/> rows into <see cref="CommunityLogRow"/>. The body is dropped here.</summary>
public static class CommunityLog
{
    /// <summary>Maps one action record to a log row. <see cref="CommunityActionRecord.Body"/> is never copied.</summary>
    /// <param name="record">Record as read from <c>community_action</c>.</param>
    /// <returns>The log row, with the body hash as lowercase hex.</returns>
    public static CommunityLogRow Project(CommunityActionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new CommunityLogRow(
            record.Id,
            record.AgentId,
            record.Platform,
            record.Action,
            record.TargetUrl,
            ToHexOrNull(record.BodySha256),
            record.Status,
            record.CreatedAt,
            record.ApprovalId,
            record.ExternalId);
    }

    /// <summary>Lowercase hex of a hash, or null when there is none.</summary>
    internal static string? ToHexOrNull(byte[]? hash) => hash is null ? null : Convert.ToHexStringLower(hash);
}

/// <summary>One page of the community log and the position to continue from.</summary>
/// <param name="Rows">Rows of the page, newest first.</param>
/// <param name="Next">Position of the last row when older rows exist, else null.</param>
public sealed record CommunityLogPage(IReadOnlyList<CommunityLogRow> Rows, CommunityLogPosition? Next);

/// <summary>Reads pages of the community log. The seam the HTTP route depends on.</summary>
public interface ICommunityLogReader
{
    /// <summary>Returns one page of log rows, newest first, with keyset continuation.</summary>
    Task<CommunityLogPage> ListPageAsync(CommunityLogFilter filter, CancellationToken ct = default);
}

/// <summary>Reads the Observatory community log over the shared <see cref="NpgsqlDataSource"/>. Read-only.</summary>
public sealed class NpgsqlCommunityLogReader(NpgsqlDataSource dataSource) : ICommunityLogReader
{
    /// <summary>Returns log rows, newest first, that match the filter.</summary>
    /// <param name="filter">Filter and page size. Null is rejected.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<CommunityLogRow>> ListAsync(CommunityLogFilter filter, CancellationToken ct = default) =>
        (await ListPageAsync(filter, ct).ConfigureAwait(false)).Rows;

    /// <inheritdoc />
    public async Task<CommunityLogPage> ListPageAsync(CommunityLogFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var rows = new List<CommunityLogRow>();

        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(CommunityLogSqlText.Select, conn);
        cmd.Parameters.Add(new NpgsqlParameter("agent_id", NpgsqlDbType.Text) { Value = (object?)filter.AgentId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("platform", NpgsqlDbType.Text) { Value = (object?)filter.Platform ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("after_created_at", NpgsqlDbType.TimestampTz)
        {
            Value = filter.After is { } a ? a.CreatedAt.ToUniversalTime() : DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("after_id", NpgsqlDbType.Bigint) { Value = filter.After is { } b ? b.Id : DBNull.Value });
        // One extra row tells whether an older page exists without a second query.
        cmd.Parameters.AddWithValue("limit", filter.Limit + 1);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Map(reader));

        if (rows.Count <= filter.Limit)
            return new CommunityLogPage(rows, null);

        rows.RemoveAt(rows.Count - 1);
        var last = rows[^1];
        return new CommunityLogPage(rows, new CommunityLogPosition(last.CreatedAt, last.Id));
    }

    private static CommunityLogRow Map(NpgsqlDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        Enum.Parse<CommunityAction>(r.GetString(3)),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : CommunityLog.ToHexOrNull(r.GetFieldValue<byte[]>(5)),
        r.GetString(6),
        r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetString(9));
}
