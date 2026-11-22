using System.Text.Json;
using Bazaar.Lucky;
using Npgsql;

namespace Bazaar.Server.Host.Lucky;

/// <summary>The Postgres <see cref="ILuckyRunStore"/> on the <c>lucky_run</c> table (migration 0021).</summary>
public sealed class PgLuckyRunStore(NpgsqlDataSource dataSource) : ILuckyRunStore
{
    private const string _countSql = "SELECT count(*) FROM lucky_run WHERE owner_id = $1 AND run_day = $2";

    private const string _upsertSql = """
        INSERT INTO lucky_run (run_id, project_id, owner_id, run_day, prompt, started_at, ends_at, participants, ideas)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9::jsonb)
        ON CONFLICT (run_id) DO UPDATE SET participants = EXCLUDED.participants, ideas = EXCLUDED.ideas
        """;

    /// <inheritdoc />
    public async Task<int> CountStartedOnAsync(Guid ownerId, DateOnly day, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_countSql);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = ownerId });
        cmd.Parameters.Add(new NpgsqlParameter<DateOnly> { TypedValue = day });
        return checked((int)(long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    /// <inheritdoc />
    public async Task SaveAsync(LuckyStoredRun stored, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var run = stored.Run;
        var participants = JsonSerializer.Serialize(run.Participants.Select(p => new { p.AgentId, p.Seed, p.Persona, p.OptedIn }));
        var ideas = JsonSerializer.Serialize(run.Ideas.Select(i => new { i.AgentId, i.Text, status = i.Status.ToString(), i.OwnerMarked }));

        await using var cmd = dataSource.CreateCommand(_upsertSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = run.RunId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = stored.ProjectId });
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = stored.OwnerId });
        cmd.Parameters.Add(new NpgsqlParameter<DateOnly> { TypedValue = stored.RunDay });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = run.Prompt });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = run.StartedAt });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = run.EndsAt });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = participants });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = ideas });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
