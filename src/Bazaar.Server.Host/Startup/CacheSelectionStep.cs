using Bazaar.Server.Garnet;
using Bazaar.Server.Host.Composition;
using Bazaar.Server.Providers;
using Bazaar.Server.Valkey;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Startup;

/// <summary>
/// Resolves the pending cache role (doc 40 section 4c.4 item 8). Registered after <see cref="StartupSequence"/> (instance lock) and after the
/// Valkey supervisor, so the managed child is already starting when the probe runs, and the stale-child sweep ran only under the lock.
/// It never fails the start: any error leaves the cache out, and the reason goes into the report.
/// </summary>
public sealed partial class CacheSelectionStep(
    IOptions<BazaarProviderOptions> options,
    ProviderCapabilityReport report,
    DeferredCacheProvider deferred,
    IEnumerable<ICacheProvider> caches,
    HostState state,
    ILogger<CacheSelectionStep> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var requested = options.Value.Cache;
        if (requested.Equals(ProviderSelector.None, StringComparison.OrdinalIgnoreCase))
        {
            deferred.Resolve(null);
            return Task.CompletedTask;
        }

        // The instance lock failed: the host is stopping. Do not start the child or probe anything, so a second instance cannot sweep the first one's child.
        if (!state.Started)
        {
            deferred.Resolve(null);
            report.Resolve(new ProviderSelection(ProviderRole.Cache, requested, null, []));
            LogSkipped(logger);
            return Task.CompletedTask;
        }

        try
        {
            // The deferred wrapper is itself an ICacheProvider, so it is not a candidate.
            var candidates = caches.Where(c => c is not DeferredCacheProvider).ToList();
            var selection = ProviderSelector.Select(ProviderRole.Cache, EffectiveRequest(requested, candidates), candidates);
            Publish(new ProviderSelection(ProviderRole.Cache, requested, selection.Provider, selection.Statuses));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type name only: selection messages can echo settings.
            var reason = $"selection failed ({ex.GetType().Name})";
            Publish(new ProviderSelection(ProviderRole.Cache, requested, null, [new ProviderStatus(requested, false, [], reason)]));
            LogFailed(logger, ex.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>An explicit request for the managed Valkey or Garnet cache selects like <c>auto</c>, so an unusable child leaves the cache out.</summary>
    private static string EffectiveRequest(string requested, IReadOnlyList<ICacheProvider> candidates) =>
        candidates.Any(c => c is ValkeyCacheProvider { IsManaged: true } or GarnetCacheProvider { IsManaged: true }
            && c.Kind.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ? ProviderSelector.Auto
            : requested;

    private void Publish(ProviderSelection selection)
    {
        // Only cache providers are candidates here, so the selected provider is an ICacheProvider or null.
        deferred.Resolve(selection.Provider as ICacheProvider);
        report.Resolve(selection);
        var line = selection.ToLogLine();
        if (logger.IsEnabled(LogLevel.Information))
        {
            LogSelection(logger, line);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Line}")]
    private static partial void LogSelection(ILogger logger, string line);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache selection failed ({ExceptionType}); the cache is left out and startup continues.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cache selection skipped: the instance lock was not acquired.")]
    private static partial void LogSkipped(ILogger logger);
}
