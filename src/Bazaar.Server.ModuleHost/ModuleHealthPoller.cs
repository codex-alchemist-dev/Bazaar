using Bazaar.Server.Modules.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.ModuleHost;

/// <summary>
/// Produces the health reports that feed <see cref="ModuleActivationHost.ApplyHealthAsync"/>. Every <see cref="IModuleHealthContract"/>
/// the host has registered is probed for liveness and readiness (doc 46 section 7) on a timer, each probe under
/// <see cref="ModuleHealthProbeRunner"/> so a slow or throwing probe is Failing, never a hang. A contract whose id is not a component
/// of this host is ignored by the activation host. The interval is <c>Bazaar:ModuleHost:HealthIntervalSeconds</c> (default 30,
/// kept between 5 and 3600).
/// </summary>
public sealed partial class ModuleHealthPoller(
    IEnumerable<IModuleHealthContract> contracts,
    ModuleActivationHost host,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ModuleHealthPoller> logger) : BackgroundService
{
    /// <summary>The default polling interval.</summary>
    public const int DefaultIntervalSeconds = 30;

    private readonly IReadOnlyList<IModuleHealthContract> _contracts = [.. contracts];

    /// <summary>The polling interval, from configuration.</summary>
    public TimeSpan Interval =>
        TimeSpan.FromSeconds(Math.Clamp(
            int.TryParse(configuration["Bazaar:ModuleHost:HealthIntervalSeconds"], out var s) ? s : DefaultIntervalSeconds, 5, 3600));

    /// <summary>Probes every contract once and applies the result. Returns the modules this deactivated.</summary>
    public async Task<IReadOnlyList<ModuleActivation>> PollOnceAsync(CancellationToken ct)
    {
        if (_contracts.Count == 0)
        {
            return [];
        }

        var reports = new List<ModuleHealthReport>(_contracts.Count);
        foreach (var contract in _contracts.DistinctBy(c => c.ModuleId, StringComparer.Ordinal))
        {
            var live = await ModuleHealthProbeRunner.RunAsync(contract.LivenessAsync, time, cancellationToken: ct).ConfigureAwait(false);
            var ready = await ModuleHealthProbeRunner.RunAsync(contract.ReadinessAsync, time, cancellationToken: ct).ConfigureAwait(false);
            reports.Add(new ModuleHealthReport(contract.ModuleId, [live, ready]));
        }

        return await host.ApplyHealthAsync(reports, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogPollFailed(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Module health poll failed; the next tick will try again.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
