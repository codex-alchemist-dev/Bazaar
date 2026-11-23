using Npgsql;

namespace Bazaar.Server.Setup.Postgres;

/// <summary>
/// <see cref="IInstallIdProvider"/> over <c>setup_install_id</c> (setup migration 0003). The first call inserts a random id with
/// <c>ON CONFLICT DO NOTHING</c>, then every call reads the one row, so racing callers and repeated runs all see the same id.
/// </summary>
public sealed class PostgresInstallIdProvider(NpgsqlDataSource dataSource) : IInstallIdProvider
{
    private const string _insert = "INSERT INTO setup_install_id (id, install_id) VALUES (1, $1) ON CONFLICT (id) DO NOTHING";
    private const string _read = "SELECT install_id FROM setup_install_id WHERE id = 1";

    /// <inheritdoc />
    public async Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default)
    {
        await using (var insert = dataSource.CreateCommand(_insert))
        {
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = Guid.CreateVersion7() });
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var read = dataSource.CreateCommand(_read);
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is Guid id ? id : throw new InvalidOperationException("The install id row is missing after it was created.");
    }
}
