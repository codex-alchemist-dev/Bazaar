using Bazaar.Contracts.Wire;
using Npgsql;

namespace Bazaar.Server.SchemaIdentity;

/// <summary>
/// Computes the schema identity from the applied rows of <c>schema_migrations</c>. It reads the history table only, so it does not
/// depend on the migration runner. The first successful read is kept: startup refuses a database that is not current, so the value
/// cannot change while the process runs.
/// </summary>
public sealed class PostgresSchemaIdentityProvider(NpgsqlDataSource dataSource) : ISchemaIdentityProvider
{
    private string? _cached;

    public async ValueTask<string?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is { } known)
            return known;
        try
        {
            var pairs = new List<(string, int)>();
            await using var cmd = dataSource.CreateCommand("SELECT namespace, version FROM schema_migrations");
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                pairs.Add((reader.GetString(0), reader.GetInt32(1)));
            if (pairs.Count == 0)
                return null;
            return _cached = SchemaDigest.Compute(pairs);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
        {
            // No history yet, or one from before namespaces: the schema has no identity to report.
            return null;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }
}
