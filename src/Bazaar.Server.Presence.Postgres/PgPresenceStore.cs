using Bazaar.Server.Presence.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Presence.Postgres;

/// <summary>
/// The durable presence store over <c>agent_activity</c> (migration <c>presence/0001_agent_activity.sql</c>, doc 23). A row is
/// replaced only when its revision is greater than the stored one, in one statement, so two writers racing on one agent cannot move
/// the revision backwards. Timestamps keep the microsecond precision the database stores.
/// </summary>
public sealed class PgPresenceStore(NpgsqlDataSource dataSource) : IDurablePresenceStore
{
    private const string _columns =
        "agent_id, revision, availability, overlay, presence, activity_kind, gc_id, turn_id, label, since, updated_at";

    /// <summary>Inserts, or replaces the row when the incoming revision is greater. Zero rows affected means the held row won.</summary>
    internal const string UpsertSql =
        "INSERT INTO agent_activity (" + _columns + ") VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) " +
        "ON CONFLICT (agent_id) DO UPDATE SET revision = EXCLUDED.revision, availability = EXCLUDED.availability, " +
        "overlay = EXCLUDED.overlay, presence = EXCLUDED.presence, activity_kind = EXCLUDED.activity_kind, " +
        "gc_id = EXCLUDED.gc_id, turn_id = EXCLUDED.turn_id, label = EXCLUDED.label, since = EXCLUDED.since, " +
        "updated_at = EXCLUDED.updated_at WHERE agent_activity.revision < EXCLUDED.revision";

    internal const string GetSql = "SELECT " + _columns + " FROM agent_activity WHERE agent_id = $1";
    internal const string GetAllSql = "SELECT " + _columns + " FROM agent_activity ORDER BY agent_id";

    /// <inheritdoc />
    public async ValueTask<bool> UpsertAsync(PresenceRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Label is { Length: > PresenceRow.MaxLabelLength })
        {
            throw new ArgumentException($"Label is longer than {PresenceRow.MaxLabelLength} characters.", nameof(row));
        }

        await using var cmd = dataSource.CreateCommand(UpsertSql);
        AddParameters(cmd, row);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async ValueTask<PresenceRow?> GetAsync(Guid agentId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(GetSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = agentId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<PresenceRow>> GetAllAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(GetAllSql);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<PresenceRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    /// <summary>Adds the eleven positional parameters of <see cref="UpsertSql"/>, in column order.</summary>
    internal static void AddParameters(NpgsqlCommand cmd, PresenceRow row)
    {
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = row.AgentId });
        cmd.Parameters.Add(new NpgsqlParameter<long> { TypedValue = row.Revision });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = PresenceColumnCodec.Availability(row.Availability) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = PresenceColumnCodec.Overlay(row.Overlay) });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = row.Presence });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = PresenceColumnCodec.Activity(row.ActivityKind) });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)row.GcId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)row.TurnId ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)row.Label ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = row.Since.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = row.UpdatedAt.ToUniversalTime() });
    }

    private static PresenceRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetInt64(1),
        PresenceColumnCodec.ParseAvailability(r.GetString(2)),
        PresenceColumnCodec.ParseOverlay(r.GetString(3)),
        r.GetString(4),
        PresenceColumnCodec.ParseActivity(r.GetString(5)),
        r.IsDBNull(6) ? null : r.GetGuid(6),
        r.IsDBNull(7) ? null : r.GetGuid(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.GetFieldValue<DateTimeOffset>(10));
}
