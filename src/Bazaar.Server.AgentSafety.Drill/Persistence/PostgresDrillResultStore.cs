using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.AgentSafety.Drill.Persistence;

/// <summary>
/// Postgres <see cref="IDrillResultStore"/> over the <c>drill_result</c> table (agent_safety migration 0006). The steps are stored as
/// jsonb; the step list is serialized with source-generated JSON, so the store is trimming and AOT safe.
/// </summary>
/// <remarks>
/// Runs under the runtime role (<c>bazaar_app</c>): SELECT and INSERT only, so a stored run cannot be edited. Instants are cut to
/// whole microseconds, the precision Postgres stores, so a write and its read-back agree.
/// </remarks>
public sealed class PostgresDrillResultStore(NpgsqlDataSource dataSource) : IDrillResultStore
{
    private const string _add =
        "INSERT INTO drill_result (id, started_at, duration_ms, passed, steps) VALUES ($1, $2, $3, $4, $5)";
    private const string _latest =
        "SELECT id, started_at, duration_ms, steps FROM drill_result ORDER BY started_at DESC LIMIT 1";

    /// <inheritdoc />
    public async Task AddAsync(DrillResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        var steps = JsonSerializer.Serialize(result.Steps.ToList(), DrillJsonContext.Default.ListDrillStepResult);
        var durationMs = (int)Math.Clamp(Math.Round(result.Duration.TotalMilliseconds), 0, int.MaxValue);

        await using var cmd = dataSource.CreateCommand(_add);
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = result.Id });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = TruncateToMicroseconds(result.StartedAt) });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = durationMs });
        cmd.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = result.Passed });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = steps, NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DrillResult?> LatestAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await using var cmd = dataSource.CreateCommand(_latest);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var steps = JsonSerializer.Deserialize(reader.GetFieldValue<string>(3), DrillJsonContext.Default.ListDrillStepResult)
            ?? throw new InvalidDataException("drill_result.steps is not a step list.");
        return new DrillResult(
            reader.GetFieldValue<Guid>(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            TimeSpan.FromMilliseconds(reader.GetInt32(2)),
            steps);
    }

    /// <summary>Cuts an instant to whole microseconds.</summary>
    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset instant)
    {
        var ticks = instant.Ticks - instant.Ticks % 10;
        return new DateTimeOffset(ticks, instant.Offset);
    }
}
