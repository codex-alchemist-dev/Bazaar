using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Bazaar.Server.ModuleHost;

/// <summary>
/// Runs <see cref="ModuleActivationHost.ActivateAllAsync"/> when the host starts and <see cref="ModuleActivationHost.DeactivateAllAsync"/>
/// when it stops, and deactivates a module when its component is disabled while the host runs. Register it after the component
/// graph and the startup sequence, because hosted services start in registration order.
/// </summary>
public sealed partial class ModuleActivationHostedService(
    ModuleActivationHost host,
    IConfiguration configuration,
    ILogger<ModuleActivationHostedService> logger) : IHostedService, IDisposable
{
    private IDisposable? _reload;

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await host.ActivateAllAsync(cancellationToken).ConfigureAwait(false);
        _reload = ChangeToken.OnChange(configuration.GetReloadToken, OnConfigurationChanged);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _reload?.Dispose();
        _reload = null;
        await host.DeactivateAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _reload?.Dispose();

    private void OnConfigurationChanged()
    {
        // A failed pass must not be swallowed silently: the set would stay in force for a module the operator disabled.
        _ = host.ApplyEnablementAsync().ContinueWith(
            t => LogEnablementFailed(logger, t.Exception!),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Applying component enablement to module permissions failed.")]
    private static partial void LogEnablementFailed(ILogger logger, Exception exception);
}
