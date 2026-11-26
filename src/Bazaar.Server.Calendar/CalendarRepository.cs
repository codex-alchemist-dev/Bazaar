using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Calendar;

/// <summary>SQL for <c>calendar_event</c> (migration 0017). Transactions belong to the caller, so the service controls locking.</summary>
public sealed class CalendarRepository(NpgsqlDataSource dataSource)
{
    private const string _columns = """
        id, kind, title_key_id, title_ct, title_nonce, starts_at, ends_at, all_day, tz, agent_id, project_id,
        affects_availability, status, source_gc_id, version
        """;

    private const string _insertSql = """
        INSERT INTO calendar_event (id, kind, title_key_id, title_ct, title_nonce, starts_at, ends_at, all_day, tz, agent_id,
                                    project_id, affects_availability, status, source_gc_id, version)
        VALUES (@id, @kind, @title_key_id, @title_ct, @title_nonce, @starts_at, @ends_at, @all_day, @tz, @agent_id,
                @project_id, @affects_availability, @status, @source_gc_id, @version)
        """;

    private const string _updateSql = """
        UPDATE calendar_event
        SET kind = @kind, title_key_id = @title_key_id, title_ct = @title_ct, title_nonce = @title_nonce,
            starts_at = @starts_at, ends_at = @ends_at, all_day = @all_day, tz = @tz, agent_id = @agent_id,
            project_id = @project_id, affects_availability = @affects_availability, status = @status,
            source_gc_id = @source_gc_id, version = @version
        WHERE id = @id AND version = @expected_version
        """;

    private const string _listSql = $"""
        SELECT {_columns} FROM calendar_event
        WHERE starts_at < @to AND ends_at > @from AND (CAST(@agent_id AS uuid) IS NULL OR agent_id = @agent_id)
        ORDER BY starts_at, id
        """;

    /// <summary>Reads one event without locking it.</summary>
    public async Task<CalendarEventRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        return await ReadOneAsync(conn, null, id, forUpdate: false, ct).ConfigureAwait(false);
    }

    /// <summary>Reads one event and locks its row until the transaction ends.</summary>
    public static Task<CalendarEventRecord?> GetForUpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct) =>
        ReadOneAsync(conn, tx, id, forUpdate: true, ct);

    /// <summary>Lists events that overlap the half-open range [<paramref name="from"/>, <paramref name="until"/>), optionally for one agent.</summary>
    public async Task<IReadOnlyList<CalendarEventRecord>> ListOverlappingAsync(
        DateTimeOffset from, DateTimeOffset until, Guid? agentId, CancellationToken ct)
    {
        if (until <= from)
            throw new ArgumentException("The range end must be after its start.", nameof(until));
        await using var conn = await dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = new NpgsqlCommand(_listSql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("from", from));
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("to", until));
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("agent_id", agentId));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<CalendarEventRecord>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>Inserts a new row. Throws on a duplicate id.</summary>
    public static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CalendarEventRecord r, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_insertSql, conn, tx);
        AddValues(cmd, r);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes a new version of a row if its stored version is still <paramref name="expectedVersion"/>.</summary>
    public static async Task<bool> UpdateAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CalendarEventRecord r, long expectedVersion, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(_updateSql, conn, tx);
        AddValues(cmd, r);
        cmd.Parameters.Add(new NpgsqlParameter<long>("expected_version", expectedVersion));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static async Task<CalendarEventRecord?> ReadOneAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, bool forUpdate, CancellationToken ct)
    {
        var sql = $"SELECT {_columns} FROM calendar_event WHERE id = @id" + (forUpdate ? " FOR UPDATE" : string.Empty);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static void AddValues(NpgsqlCommand cmd, CalendarEventRecord r)
    {
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("id", r.Id));
        cmd.Parameters.Add(new NpgsqlParameter<string>("kind", CalendarRules.ToSql(r.Kind)));
        cmd.Parameters.Add(new NpgsqlParameter<Guid>("title_key_id", r.TitleKeyId));
        cmd.Parameters.Add(new NpgsqlParameter<byte[]>("title_ct", r.TitleCiphertext));
        cmd.Parameters.Add(new NpgsqlParameter<byte[]?>("title_nonce", r.TitleNonce));
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("starts_at", r.StartsAt));
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("ends_at", r.EndsAt));
        cmd.Parameters.Add(new NpgsqlParameter<bool>("all_day", r.AllDay));
        cmd.Parameters.Add(new NpgsqlParameter<string>("tz", r.Tz));
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("agent_id", r.AgentId));
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("project_id", r.ProjectId));
        cmd.Parameters.Add(new NpgsqlParameter<bool>("affects_availability", r.AffectsAvailability));
        cmd.Parameters.Add(new NpgsqlParameter<string>("status", CalendarRules.ToSql(r.Status)));
        cmd.Parameters.Add(new NpgsqlParameter<Guid?>("source_gc_id", r.SourceGcId));
        cmd.Parameters.Add(new NpgsqlParameter<long>("version", r.Version));
    }

    private static CalendarEventRecord Read(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Kind = CalendarRules.KindFromSql(reader.GetString(1)),
        TitleKeyId = reader.GetGuid(2),
        TitleCiphertext = reader.GetFieldValue<byte[]>(3),
        TitleNonce = reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4),
        StartsAt = reader.GetFieldValue<DateTimeOffset>(5),
        EndsAt = reader.GetFieldValue<DateTimeOffset>(6),
        AllDay = reader.GetBoolean(7),
        Tz = reader.GetString(8),
        AgentId = reader.IsDBNull(9) ? null : reader.GetGuid(9),
        ProjectId = reader.IsDBNull(10) ? null : reader.GetGuid(10),
        AffectsAvailability = reader.GetBoolean(11),
        Status = CalendarRules.StatusFromSql(reader.GetString(12)),
        SourceGcId = reader.IsDBNull(13) ? null : reader.GetGuid(13),
        Version = reader.GetInt64(14),
    };
}
