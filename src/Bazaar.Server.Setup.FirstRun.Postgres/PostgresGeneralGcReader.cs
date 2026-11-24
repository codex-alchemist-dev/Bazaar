using Npgsql;

namespace Bazaar.Server.Setup.FirstRun.Postgres;

/// <summary>Reads the seed marker for the general GC and returns it only while that GC is not archived.</summary>
public sealed class PostgresGeneralGcReader(NpgsqlDataSource dataSource) : IGeneralGcReader
{
    private const string _sql = """
        SELECT s.entity_id FROM firstrun_seed s
        JOIN gc ON gc.gc_id = s.entity_id AND gc.archived_at IS NULL
        WHERE s.kind = 'general_gc'
        """;

    /// <inheritdoc />
    public async Task<Guid?> GetGeneralGcIdAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(_sql);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as Guid?;
    }
}
