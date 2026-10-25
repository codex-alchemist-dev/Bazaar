using Npgsql;

namespace Bazaar.Testing.Postgres;

/// <summary>
/// A throwaway PostgreSQL database for one test class, cloned from the per-run migrated template.
/// <para>
/// Bazaar.Testing does not reference xunit, so the fixture is plain; in a test project declare
/// <c>public sealed class Db : PgFixture, IAsyncLifetime;</c> and use <c>IClassFixture&lt;Db&gt;</c>.
/// </para>
/// </summary>
public class PgFixture : IAsyncDisposable
{
    private PgTemplate? _template;
    private NpgsqlDataSource? _dataSource;

    /// <summary>Name of this class's database.</summary>
    public string DatabaseName { get; private set; } = "";

    /// <summary>Connection string for this class's database.</summary>
    public string ConnectionString { get; private set; } = "";

    /// <summary>Pooled data source for this class's database.</summary>
    public NpgsqlDataSource DataSource =>
        _dataSource ?? throw new InvalidOperationException("PgFixture is not initialised.");

    public async ValueTask InitializeAsync()
    {
        _template = await PgTemplate.GetSharedAsync().ConfigureAwait(false);
        DatabaseName = await _template.CloneAsync(GetType().Name).ConfigureAwait(false);
        ConnectionString = _template.ConnectionStringFor(DatabaseName);
        _dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async ValueTask<NpgsqlConnection> OpenConnectionAsync(CancellationToken ct = default) =>
        await DataSource.OpenConnectionAsync(ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_dataSource is not null)
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        if (_template is not null && DatabaseName.Length > 0)
            await _template.DropAsync(DatabaseName).ConfigureAwait(false);
    }
}
