using System.Collections.Immutable;
using System.Text.Json;
using Bazaar.Lucky;
using Npgsql;

namespace Bazaar.Server.Host.Lucky;

/// <summary>The Postgres <see cref="ILuckyRunStore"/> on the <c>lucky_run</c> table (component migration lucky 0001).</summary>
public sealed class PgLuckyRunStore(NpgsqlDataSource dataSource) : ILuckyRunStore
{
    private const string _countSql = "SELECT count(*) FROM lucky_run WHERE owner_id = $1 AND run_day = $2";

    private const string _loadSql = """
        SELECT run_id, project_id, owner_id, run_day, prompt, started_at, ends_at,
               participants::text, ideas::text, gc_id, idea_messages::text
        FROM lucky_run WHERE run_id = $1
        """;

    private const string _upsertSql = """
        INSERT INTO lucky_run (run_id, project_id, owner_id, run_day, prompt, started_at, ends_at, participants, ideas, gc_id, idea_messages)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $9::jsonb, $10, $11::jsonb)
        ON CONFLICT (run_id) DO UPDATE SET
            participants = EXCLUDED.participants,
            ideas = EXCLUDED.ideas,
            gc_id = EXCLUDED.gc_id,
            idea_messages = EXCLUDED.idea_messages
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
        var ideas = JsonSerializer.Serialize(run.Ideas.Select(i => new { i.AgentId, i.Text, Status = i.Status.ToString(), i.OwnerMarked }));
        var messages = JsonSerializer.Serialize(stored.Messages.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value));

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
        cmd.Parameters.Add(new NpgsqlParameter<Guid?> { TypedValue = stored.GcId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = messages });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<LuckyStoredRun?> LoadAsync(string runId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(_loadSql);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = runId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var participants = ParseParticipants(reader.GetString(7));
        var ideas = ParseIdeas(reader.GetString(8));
        var run = new LuckyRun(
            reader.GetString(0),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            participants,
            ideas);
        var gcId = reader.IsDBNull(9) ? (Guid?)null : reader.GetGuid(9);
        var messages = ParseMessages(reader.GetString(10));
        return new LuckyStoredRun(run, reader.GetGuid(1), reader.GetGuid(2), reader.GetFieldValue<DateOnly>(3), gcId, messages);
    }

    private static ImmutableArray<LuckyParticipant> ParseParticipants(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => new LuckyParticipant(
            e.GetProperty("AgentId").GetString()!,
            e.GetProperty("Seed").GetInt32(),
            e.GetProperty("Persona").GetString()!,
            e.GetProperty("OptedIn").GetBoolean()))];
    }

    private static ImmutableArray<LuckyIdea> ParseIdeas(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.EnumerateArray().Select(e => new LuckyIdea(
            e.GetProperty("AgentId").GetString()!,
            e.GetProperty("Text").GetString() ?? string.Empty,
            Enum.Parse<LuckyIdeaStatus>(e.GetProperty("Status").GetString()!),
            e.GetProperty("OwnerMarked").GetBoolean()))];
    }

    private static ImmutableDictionary<int, Guid> ParseMessages(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var builder = ImmutableDictionary.CreateBuilder<int, Guid>();
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            builder[int.Parse(property.Name, System.Globalization.CultureInfo.InvariantCulture)] = property.Value.GetGuid();
        }

        return builder.ToImmutable();
    }
}
