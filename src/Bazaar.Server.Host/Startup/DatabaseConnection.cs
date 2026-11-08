using Bazaar.Server.Hosting;
using Bazaar.Server.Secrets;
using Npgsql;

namespace Bazaar.Server.Host.Startup;

/// <summary>Resolves the database secret reference once and shares one <see cref="NpgsqlDataSource"/> (doc 04). The connection string never reaches logs.</summary>
public sealed class DatabaseConnection(
    IOptions<BazaarHostOptions> options,
    ISecretResolver secrets,
    Bazaar.Components.ManagedPostgres.IManagedPostgresConnectionProvider? managed = null) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NpgsqlDataSource? _dataSource;

    public async ValueTask<NpgsqlDataSource> GetAsync(CancellationToken ct)
    {
        if (_dataSource is not null)
        {
            return _dataSource;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_dataSource is null)
            {
                // Managed mode starts the private cluster here, so the first connection waits for it. External mode returns null.
                var connectionString = managed is null ? null : await managed.GetConnectionStringAsync(ct);
                connectionString ??= await secrets.ResolveAsync(options.Value.DatabaseConnectionReference, ct);
                _dataSource = NpgsqlDataSource.Create(connectionString);
            }

            return _dataSource;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync();
        }

        _gate.Dispose();
    }
}
