using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Runs the managed cluster. The first caller (the host's database connection or the hosted service) starts it, under a
/// lock, so the cluster is ready before anything opens a connection. In External mode it does nothing. The first start
/// runs initdb and saves the superuser password; later starts load it.
/// </summary>
public sealed class ManagedPostgresHostedService(
    IOptions<ManagedPostgresOptions> options,
    IPostgresCommandRunner runner,
    IEnumerable<IManagedPasswordStore> passwordStores,
    ManagedPostgresEndpoint endpoint) : IHostedService, IAsyncDisposable, IManagedPostgresConnectionProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PostgresProcessController? _controller;

    public Task<string?> GetConnectionStringAsync(CancellationToken cancellationToken) => EnsureRunningAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => EnsureRunningAsync(cancellationToken);

    /// <summary>Starts the cluster once and returns its connection string; null in External mode.</summary>
    public async Task<string?> EnsureRunningAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode != DatabaseMode.Managed)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (endpoint.Current is { } running)
            {
                return ConnectionString(running);
            }

            var store = passwordStores.FirstOrDefault()
                ?? throw new InvalidOperationException("Managed PostgreSQL needs a superuser password store, and none is registered.");

            var paths = new BundleLayout(settings.BundleRoot, settings.DataRoot).Resolve(settings.Major);
            _controller = new PostgresProcessController(
                new PostgresProcessOptions
                {
                    BinDirectory = paths.Bin,
                    DataDirectory = paths.DataDirectory,
                    Port = settings.Port,
                    ListenAddress = settings.ListenAddress,
                    SuperuserName = settings.SuperuserName,
                },
                runner);

            if (!File.Exists(Path.Combine(paths.DataDirectory, "PG_VERSION")))
            {
                await _controller.InitDbAsync((password, token) => new ValueTask(store.SaveAsync(password, token)), cancellationToken);
            }

            await _controller.StartAsync(cancellationToken);

            var password = await store.LoadAsync(cancellationToken)
                ?? throw new InvalidOperationException("The managed superuser password is missing from the store.");

            // Loopback is how the host reaches a wildcard listener; the controller probes the same address.
            var host = settings.ListenAddress switch
            {
                "0.0.0.0" => "127.0.0.1",
                "::" => "::1",
                var address => address,
            };
            var connection = new ManagedPostgresConnectionInfo(host, settings.Port, settings.SuperuserName, password, "postgres");
            endpoint.Set(connection);
            return ConnectionString(connection);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string ConnectionString(ManagedPostgresConnectionInfo connection) => new NpgsqlConnectionStringBuilder
    {
        Host = connection.Host,
        Port = connection.Port,
        Username = connection.Username,
        Password = connection.Password,
        Database = connection.Database,
    }.ConnectionString;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_controller is not null)
        {
            await _controller.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null)
        {
            await _controller.DisposeAsync();
        }

        _gate.Dispose();
    }
}
