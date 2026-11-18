using System.Text.Json.Nodes;
using Bazaar.Server.Playtest.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Bazaar.Server.Playtest;

/// <summary>Postgres store over <c>playtest_run</c> (migration 0001 in the <c>playtest</c> namespace). JSON is built by hand so the type stays AOT friendly.</summary>
public sealed class PostgresPlaytestRunStore(NpgsqlDataSource dataSource) : IPlaytestRunStore
{
    private const string InsertSql = """
        INSERT INTO playtest_run (id, engine, build_ref, mode, scenario_id, status, state_dump_hash, project_hash_before,
                                  project_hash_after, assertion_specs, timeout_seconds, assertion_results, created_at)
        VALUES (@id, @engine, @build_ref, @mode, @scenario_id, @status, @state_dump_hash, @before, @after,
                @specs, @timeout, @results, @created_at)
        """;

    private const string SelectSql = """
        SELECT engine, build_ref, scenario_id, status, state_dump_hash, project_hash_before, project_hash_after,
               assertion_specs::text, timeout_seconds, assertion_results::text, created_at
        FROM playtest_run WHERE id = @id
        """;

    public async Task SaveAsync(PlaytestResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        await using var command = dataSource.CreateCommand(InsertSql);
        command.Parameters.AddWithValue("id", result.RunId);
        command.Parameters.AddWithValue("engine", EngineText(result.Scenario.Engine));
        command.Parameters.AddWithValue("build_ref", result.Scenario.BuildRef);
        command.Parameters.AddWithValue("mode", "tier_a");
        command.Parameters.AddWithValue("scenario_id", result.Scenario.ScenarioId);
        command.Parameters.AddWithValue("status", StatusText(result.Status));
        command.Parameters.AddWithValue("state_dump_hash", (object?)result.StateDumpHash ?? DBNull.Value);
        command.Parameters.AddWithValue("before", (object?)result.ProjectHashBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("after", (object?)result.ProjectHashAfter ?? DBNull.Value);
        command.Parameters.Add(new NpgsqlParameter("specs", NpgsqlDbType.Jsonb) { Value = SpecsJson(result.Scenario.Assertions) });
        command.Parameters.AddWithValue("timeout", (int)Math.Ceiling(result.Scenario.Timeout.TotalSeconds));
        command.Parameters.Add(new NpgsqlParameter("results", NpgsqlDbType.Jsonb) { Value = ResultsJson(result.Assertions) });
        command.Parameters.AddWithValue("created_at", result.CreatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<PlaytestResult?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(SelectSql);
        command.Parameters.AddWithValue("id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var specs = ((JsonArray)JsonNode.Parse(reader.GetString(7))!)
            .Select(n => new PlaytestAssertion((string)n!["id"]!, (string)n["key"]!, (string)n["expected"]!))
            .ToList();
        var results = ((JsonArray)JsonNode.Parse(reader.GetString(9))!)
            .Select(n => new AssertionResult((string)n!["id"]!, (bool)n["passed"]!, (string?)n["actual"]))
            .ToList();
        var scenario = new PlaytestScenario(
            ParseEngine(reader.GetString(0)), reader.GetString(1), reader.GetString(2), specs, TimeSpan.FromSeconds(reader.GetInt32(8)));

        return new PlaytestResult(
            runId, scenario, PlaytestMode.TierA, ParseStatus(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4), results,
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    private static string SpecsJson(IReadOnlyList<PlaytestAssertion> assertions)
    {
        var array = new JsonArray();
        foreach (var a in assertions)
        {
            array.Add((JsonNode)new JsonObject { ["id"] = a.Id, ["key"] = a.Key, ["expected"] = a.Expected });
        }

        return array.ToJsonString();
    }

    private static string ResultsJson(IReadOnlyList<AssertionResult> results)
    {
        var array = new JsonArray();
        foreach (var r in results)
        {
            array.Add((JsonNode)new JsonObject { ["id"] = r.AssertionId, ["passed"] = r.Passed, ["actual"] = r.Actual });
        }

        return array.ToJsonString();
    }

    private static string EngineText(PlaytestEngine engine) => engine switch
    {
        PlaytestEngine.Godot => "godot",
        PlaytestEngine.Luau => "luau",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static PlaytestEngine ParseEngine(string text) => text switch
    {
        "godot" => PlaytestEngine.Godot,
        "luau" => PlaytestEngine.Luau,
        _ => throw new InvalidOperationException($"Unknown playtest engine '{text}'."),
    };

    private static string StatusText(PlaytestStatus status) => status switch
    {
        PlaytestStatus.Passed => "passed",
        PlaytestStatus.AssertionsFailed => "assertions_failed",
        PlaytestStatus.ProjectChanged => "project_changed",
        PlaytestStatus.TimedOut => "timed_out",
        PlaytestStatus.HostFailed => "host_failed",
        PlaytestStatus.ProfileRejected => "profile_rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static PlaytestStatus ParseStatus(string text) => text switch
    {
        "passed" => PlaytestStatus.Passed,
        "assertions_failed" => PlaytestStatus.AssertionsFailed,
        "project_changed" => PlaytestStatus.ProjectChanged,
        "timed_out" => PlaytestStatus.TimedOut,
        "host_failed" => PlaytestStatus.HostFailed,
        "profile_rejected" => PlaytestStatus.ProfileRejected,
        _ => throw new InvalidOperationException($"Unknown playtest status '{text}'."),
    };
}
