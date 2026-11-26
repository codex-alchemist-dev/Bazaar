using Npgsql;

namespace Bazaar.Server.TimeMachine;

/// <summary>
/// <see cref="IReplayMarkerSource"/> over the <c>audit_log</c> table (docs/design/15, audit log). It selects only <c>ts</c> and
/// <c>kind</c>: never the actor or the payload, which are per-person encrypted and owner-only. A database without the table (the
/// audit component is not installed) yields no markers. It needs SELECT only, which the runtime role already has.
/// </summary>
/// <param name="dataSource">The shared data source.</param>
public sealed class PostgresReplayMarkerSource(NpgsqlDataSource dataSource) : IReplayMarkerSource
{
    private const string _exists = "SELECT to_regclass('audit_log') IS NOT NULL";
    private const string _list = "SELECT ts, kind FROM audit_log WHERE ts >= $1 AND ts <= $2 ORDER BY ts, seq LIMIT $3";

    private static readonly DateTimeOffset _minStamp = new(1, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _maxStamp = new(9999, 12, 31, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReplayMarker>> ListAsync(DateTimeOffset since, DateTimeOffset until, int limit, CancellationToken ct)
    {
        await using (var check = dataSource.CreateCommand(_exists))
        {
            if (await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            {
                return [];
            }
        }

        // Postgres timestamps cap at year 294276 and floor at 4713 BC; clamp the open-ended bounds the lister passes.
        var low = since < _minStamp ? _minStamp : since;
        var high = until > _maxStamp ? _maxStamp : until;
        await using var cmd = dataSource.CreateCommand(_list);
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = low.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = high.ToUniversalTime() });
        cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = Math.Max(1, limit) });
        var found = new List<ReplayMarker>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            found.Add(new ReplayMarker(r.GetFieldValue<DateTimeOffset>(0), r.GetString(1)));
        }

        return found;
    }
}
