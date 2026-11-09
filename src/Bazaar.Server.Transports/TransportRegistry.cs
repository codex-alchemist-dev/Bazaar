using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Transports;

/// <summary>
/// Starts the enabled modules, collects their offers and hands them to the pairing and client layers. Doc 39 section 1
/// and section 4. A failing module is marked <see cref="TransportModuleState.Failed"/> and does not stop the others.
/// </summary>
public sealed class TransportRegistry : IAsyncDisposable
{
    private readonly List<ITransportModule> _modules;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ModuleStatus> _status = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a registry over the given modules. Duplicate ids are rejected.</summary>
    public TransportRegistry(IEnumerable<ITransportModule> modules, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(modules);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var list = modules.ToList();
        var duplicate = list.GroupBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Duplicate transport module id '{duplicate.Key}'.", nameof(modules));
        }

        _modules = list.OrderBy(m => m.Priority).ThenBy(m => m.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Modules in client try order.</summary>
    public IReadOnlyList<ITransportModule> Modules => _modules;

    /// <summary>
    /// Starts each module whose setting enables it, in priority order. Errors are recorded per module. Returns the
    /// status of every module after the start attempt.
    /// </summary>
    public async Task<IReadOnlyList<ModuleStatus>> StartAsync(ReachabilityContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var module in _modules)
        {
            var enabled = context.Settings.Read($"transport.{module.Id}.enabled", module.DefaultEnabled);
            if (!enabled)
            {
                SetStatus(new ModuleStatus(module.Id, TransportModuleState.Disabled, null, []));
                continue;
            }

            module.StatusChanged += OnModuleStatusChanged;
            try
            {
                await module.StartAsync(context, cancellationToken).ConfigureAwait(false);
                SetStatus(module.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                TransportLog.StartFailed(_logger, module.Id, ex);
                SetStatus(new ModuleStatus(module.Id, TransportModuleState.Failed, ex.GetType().Name, []));
            }
        }

        return Snapshot();
    }

    /// <summary>All offers from running modules that pass <see cref="TransportOfferRules.Validate"/>, in client try order.</summary>
    public IReadOnlyList<TransportOffer> CurrentOffers()
    {
        lock (_gate)
        {
            var offers = new List<TransportOffer>();
            foreach (var module in _modules)
            {
                if (!_status.TryGetValue(module.Id, out var status))
                {
                    continue;
                }

                foreach (var offer in status.Offers)
                {
                    var reason = TransportOfferRules.Validate(offer);
                    if (reason is null)
                    {
                        offers.Add(offer);
                    }
                    else
                    {
                        TransportLog.OfferDropped(_logger, offer.ModuleId, reason);
                    }
                }
            }

            return offers;
        }
    }

    /// <summary>Status of every module, in client try order.</summary>
    public IReadOnlyList<ModuleStatus> Snapshot()
    {
        lock (_gate)
        {
            return _modules
                .Select(m => _status.TryGetValue(m.Id, out var s) ? s : new ModuleStatus(m.Id, TransportModuleState.Disabled, null, []))
                .ToList();
        }
    }

    /// <summary>Stops every started module in reverse priority order. Errors are logged and do not stop the rest.</summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        for (var i = _modules.Count - 1; i >= 0; i--)
        {
            var module = _modules[i];
            if (!_status.TryGetValue(module.Id, out var status) || status.State == TransportModuleState.Disabled)
            {
                continue;
            }

            module.StatusChanged -= OnModuleStatusChanged;
            try
            {
                await module.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TransportLog.StopFailed(_logger, module.Id, ex);
            }

            SetStatus(new ModuleStatus(module.Id, TransportModuleState.Disabled, null, []));
        }
    }

    /// <summary>Stops all modules.</summary>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private void OnModuleStatusChanged(object? sender, ModuleStatus status) => SetStatus(status);

    private void SetStatus(ModuleStatus status)
    {
        lock (_gate)
        {
            _status[status.ModuleId] = status;
        }
    }
}
