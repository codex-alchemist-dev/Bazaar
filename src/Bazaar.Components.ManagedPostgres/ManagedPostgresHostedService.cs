using Bazaar.Components.ManagedPostgres.Bundle;
using Bazaar.Components.ManagedPostgres.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Components.ManagedPostgres;

/// <summary>
/// Starts the managed cluster before the host migrates or reports ready, and stops it when the host stops. In External
/// mode it does nothing. The first start runs initdb and saves the superuser password; later starts load it.
/// </summary>
public sealed class ManagedPostgresHostedService(
    IOptions<ManagedPostgresOptions> options,
    IPostgresCommandRunner runner,
    IEnumerable<IManagedPasswordStore> passwordStores,
    ManagedPostgresEndpoint endpoint) : IHostedService, IAsyncDisposable
{
    private PostgresProcessController? _controller;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (settings.Mode != DatabaseMode.Managed)
        {
            return;
        }

        var store = passwordStores.FirstOrDefault()
            ?? throw new InvalidOperationException("Managed PostgreSQL needs a superuser password store, and none is registered.");

        var paths = new BundleLayout(settings.BundleRoot, settings.DataRoot).Resolve(settings.Major);
        var processOptions = new PostgresProcessOptions
        {
            BinDirectory = paths.Bin,
            DataDirectory = paths.DataDirectory,
            Port = settings.Port,
            ListenAddress = settings.ListenAddress,
            SuperuserName = settings.SuperuserName,
        };

        _controller = new PostgresProcessController(processOptions, runner);

        if (!File.Exists(Path.Combine(paths.DataDirectory, "PG_VERSION")))
        {
            await _controller.InitDbAsync((password, token) => new ValueTask(store.SaveAsync(password, token)), cancellationToken);
        }

        await _controller.StartAsync(cancellationToken);

        var password = await store.LoadAsync(cancellationToken)
            ?? throw new InvalidOperationException("The managed superuser password is missing from the store.");

        // A wildcard listen address is reached through loopback; the controller already probes it the same way.
        var host = settings.ListenAddress is "0.0.0.0" or "::" ? (settings.ListenAddress == "::" ? "::1" : "127.0.0.1") : settings.ListenAddress;
        endpoint.Set(new ManagedPostgresConnectionInfo(host, settings.Port, settings.SuperuserName, password, "postgres"));
    }

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
    }
}
