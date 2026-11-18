using Bazaar.Server.Hive.Abstractions;
using Npgsql;

namespace Bazaar.Server.Hive;

/// <summary>Raw Npgsql over <c>hive_copy</c> (migration components/hive/0001), on the shared data source.</summary>
public sealed class PostgresHiveCopyStore(NpgsqlDataSource dataSource) : IHiveCopyStore
{
    private const string _columns =
        "copy_id, from_project, to_project, kind, origin_memory, origin_kind, trust, title, body, path, state, held_at, review_until, released_at, deprecated_at";

    public async Task<bool> TryInsertAsync(HiveCopy copy, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"INSERT INTO hive_copy ({_columns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) ON CONFLICT DO NOTHING");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = copy.CopyId });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.FromProject.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.ToProject.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.Kind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.OriginMemory });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.OriginKind });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.Trust.ToString() });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.Title });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.Body });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = copy.Path });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = StateText(copy.State) });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = copy.HeldAt });
        cmd.Parameters.Add(Nullable(copy.ReviewUntil));
        cmd.Parameters.Add(Nullable(copy.ReleasedAt));
        cmd.Parameters.Add(Nullable(copy.DeprecatedAt));
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<HiveCopy?> FindAsync(ProjectId source, string originMemory, ProjectId target, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM hive_copy WHERE from_project = $1 AND origin_memory = $2 AND to_project = $3");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = source.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = originMemory });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.Value });
        var rows = await ReadAsync(cmd, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<HiveCopy?> GetAsync(Guid copyId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM hive_copy WHERE copy_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = copyId });
        var rows = await ReadAsync(cmd, ct).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<IReadOnlyList<HiveCopy>> ListDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM hive_copy WHERE state = 'in_review' AND review_until IS NOT NULL AND review_until <= $1 ORDER BY review_until, copy_id");
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = now });
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HiveCopy>> ListLiveAsync(ProjectId source, ProjectId target, string kind, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            $"SELECT {_columns} FROM hive_copy WHERE from_project = $1 AND to_project = $2 AND kind = $3 AND state <> 'deprecated' ORDER BY held_at, copy_id");
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = source.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = target.Value });
        cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = kind });
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HiveCopy>> ListAsync(CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand($"SELECT {_columns} FROM hive_copy ORDER BY held_at, copy_id");
        return await ReadAsync(cmd, ct).ConfigureAwait(false);
    }

    public async Task<bool> MarkActiveAsync(Guid copyId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE hive_copy SET state = 'active', released_at = $2 WHERE copy_id = $1 AND state = 'in_review'");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = copyId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    public async Task<bool> MarkDeprecatedAsync(Guid copyId, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE hive_copy SET state = 'deprecated', deprecated_at = $2 WHERE copy_id = $1 AND state <> 'deprecated'");
        cmd.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = copyId });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = at });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
    }

    private static NpgsqlParameter<DateTimeOffset?> Nullable(DateTimeOffset? value) => new() { TypedValue = value };

    private static string StateText(HiveCopyState state) => state switch
    {
        HiveCopyState.InReview => "in_review",
        HiveCopyState.Active => "active",
        HiveCopyState.Deprecated => "deprecated",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static HiveCopyState ParseState(string text) => text switch
    {
        "in_review" => HiveCopyState.InReview,
        "active" => HiveCopyState.Active,
        "deprecated" => HiveCopyState.Deprecated,
        _ => throw new InvalidOperationException($"Unknown hive copy state '{text}'."),
    };

    private static async Task<IReadOnlyList<HiveCopy>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<HiveCopy>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HiveCopy(
                reader.GetGuid(0),
                new ProjectId(reader.GetString(1)),
                new ProjectId(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                Enum.Parse<HiveTrust>(reader.GetString(6)),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                ParseState(reader.GetString(10)),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14)));
        }

        return list;
    }
}
