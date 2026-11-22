using Bazaar.Server.PsychWard.Abstractions;
using Npgsql;

namespace Bazaar.Server.PsychWard;

/// <summary>Postgres <see cref="IVentStore"/> (<c>vent</c> and <c>vent_open_failure</c>, migration components/psych_ward/0001). Keyed by failure reference.</summary>
public sealed class PgVentStore(NpgsqlDataSource dataSource) : IVentStore
{
    private const string _columns = "failure_ref, agent_id, what_happened, what_would_change, unsure, status, created_at, publish_target";

    /// <inheritdoc />
    public async Task<VentDraft> GetOrAddAsync(VentDraft candidate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await using (var insert = dataSource.CreateCommand("""
            INSERT INTO vent (failure_ref, agent_id, what_happened, what_would_change, unsure, status, created_at, publish_target)
            VALUES (@ref, @agent, @happened, @change, @unsure, @status, @created, @target)
            ON CONFLICT (failure_ref) DO NOTHING
            """))
        {
            Bind(insert, candidate);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(candidate.FailureRef, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A vent vanished right after it was stored.");
    }

    /// <inheritdoc />
    public async Task<VentDraft?> GetAsync(string failureRef, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM vent WHERE failure_ref = @ref");
        cmd.Parameters.AddWithValue("ref", failureRef);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(VentDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var cmd = dataSource.CreateCommand("UPDATE vent SET status = @status, publish_target = @target WHERE failure_ref = @ref");
        cmd.Parameters.AddWithValue("ref", draft.FailureRef);
        cmd.Parameters.AddWithValue("status", StatusName(draft.Status));
        cmd.Parameters.AddWithValue("target", (object?)draft.PublishTarget ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VentDraft>> ListByStatusAsync(VentStatus status, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM vent WHERE status = @status ORDER BY created_at");
        cmd.Parameters.AddWithValue("status", StatusName(status));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var vents = new List<VentDraft>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            vents.Add(Read(reader));
        }

        return vents;
    }

    /// <inheritdoc />
    public async Task AddOpenFailureAsync(Guid agentId, string failureRef, CancellationToken cancellationToken)
    {
        // A failure that already has a vent is not open.
        await using var cmd = dataSource.CreateCommand("""
            INSERT INTO vent_open_failure (failure_ref, agent_id)
            SELECT @ref, @agent WHERE NOT EXISTS (SELECT 1 FROM vent WHERE failure_ref = @ref)
            ON CONFLICT (failure_ref) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("ref", failureRef);
        cmd.Parameters.AddWithValue("agent", agentId);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ResolveFailureAsync(string failureRef, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM vent_open_failure WHERE failure_ref = @ref");
        cmd.Parameters.AddWithValue("ref", failureRef);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasOpenFailureAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var cmd = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM vent_open_failure WHERE agent_id = @agent)");
        cmd.Parameters.AddWithValue("agent", agentId);
        return (bool)(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static void Bind(NpgsqlCommand cmd, VentDraft v)
    {
        cmd.Parameters.AddWithValue("ref", v.FailureRef);
        cmd.Parameters.AddWithValue("agent", v.AgentId);
        cmd.Parameters.AddWithValue("happened", v.WhatHappened);
        cmd.Parameters.AddWithValue("change", v.WhatWouldChange);
        cmd.Parameters.AddWithValue("unsure", v.Unsure);
        cmd.Parameters.AddWithValue("status", StatusName(v.Status));
        cmd.Parameters.AddWithValue("created", v.CreatedAt);
        cmd.Parameters.AddWithValue("target", (object?)v.PublishTarget ?? DBNull.Value);
    }

    private static VentDraft Read(NpgsqlDataReader r) => new(
        r.GetString(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        ParseStatus(r.GetString(5)),
        r.GetFieldValue<DateTimeOffset>(6),
        r.IsDBNull(7) ? null : r.GetString(7));

    private static string StatusName(VentStatus status) => status switch
    {
        VentStatus.Draft => "draft",
        VentStatus.Held => "held",
        VentStatus.Published => "published",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    private static VentStatus ParseStatus(string name) => name switch
    {
        "draft" => VentStatus.Draft,
        "held" => VentStatus.Held,
        "published" => VentStatus.Published,
        _ => throw new InvalidOperationException($"Unknown vent status '{name}' in vent."),
    };
}
