using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// Durable <see cref="IApplyResultStore"/> over <c>setup_apply_result</c> (setup migration 0001). The first result saved for a plan
/// hash wins: a later save for the same hash inserts nothing, so a repeat apply replays the first outcome across restarts.
/// </summary>
public sealed class PostgresApplyResultStore(NpgsqlDataSource dataSource) : IApplyResultStore
{
    private const string _get = "SELECT result::text FROM setup_apply_result WHERE plan_hash = $1";
    private const string _save =
        "INSERT INTO setup_apply_result (plan_hash, exit_code, result) VALUES ($1, $2, $3) ON CONFLICT (plan_hash) DO NOTHING";

    /// <inheritdoc />
    public async Task<SetupApplyResult?> TryGetAsync(string planHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        await using var cmd = dataSource.CreateCommand(_get);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = planHash });
        var json = (string?)await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize(json, SetupPostgresJsonContext.Default.SetupApplyResult);
    }

    /// <inheritdoc />
    public async Task SaveAsync(string planHash, SetupApplyResult result, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planHash);
        ArgumentNullException.ThrowIfNull(result);
        await using var cmd = dataSource.CreateCommand(_save);
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = planHash });
        cmd.Parameters.Add(new NpgsqlParameter<short> { TypedValue = (short)result.ExitCode });
        cmd.Parameters.Add(new NpgsqlParameter<string>
        {
            TypedValue = JsonSerializer.Serialize(result, SetupPostgresJsonContext.Default.SetupApplyResult),
            NpgsqlDbType = NpgsqlDbType.Jsonb,
        });
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
