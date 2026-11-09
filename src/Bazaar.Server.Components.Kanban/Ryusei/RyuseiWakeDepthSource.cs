using Npgsql;

namespace Bazaar.Server.Kanban.Ryusei;

/// <summary>
/// SQL-first <see cref="IRyuseiWakeDepthSource"/> over <c>ryusei_fire</c> joined to <c>ryusei_trigger</c> (doc 38, Chain depth).
/// A read only: it takes no lock and changes no row.
/// </summary>
public sealed class RyuseiWakeDepthSource(NpgsqlDataSource dataSource) : IRyuseiWakeDepthSource
{
    /// <summary>The depth read. Exposed so the pure test can check the tables and column it names.</summary>
    public const string ChainDepthSql = """
        SELECT t.chain_depth
        FROM ryusei_fire AS f
        JOIN ryusei_trigger AS t ON t.trigger_id = f.trigger_id
        WHERE f.fire_id = @fire_id
        """;

    public async Task<int?> ChainDepthForFireAsync(Guid fireId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(ChainDepthSql);
        cmd.Parameters.AddWithValue("fire_id", fireId);

        // chain_depth is NOT NULL, so a null scalar means no fire row matched.
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is int depth ? depth : null;
    }
}
