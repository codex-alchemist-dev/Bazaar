using Bazaar.Server.OutcomeLedger.Abstractions;
using Bazaar.Server.PsychWard.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Bazaar.Server.Reputation;

/// <summary>
/// Hands the Psych Ward watcher every agent and tag a newly recorded failed outcome (x = 0) touches (doc 26, section 1). A good
/// outcome cannot start a failure streak, so it is skipped. The watcher is already best effort.
/// </summary>
public sealed class WardOutcomeListener(OutcomeWardWatcher watcher) : IOutcomeListener
{
    public async Task OnRecordedAsync(Guid outcomeId, OutcomeRecord resolved, CancellationToken cancellationToken)
    {
        if (resolved.X != 0m)
        {
            return;
        }

        foreach (var a in resolved.Attributions)
        {
            await watcher.CheckAsync(a.AgentId, a.Tag, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Composition for the reputation side of outcome recording.</summary>
public static class ReputationOutcomeRegistration
{
    /// <summary>
    /// Registers <see cref="WardOutcomeListener"/> so a recorded failed outcome can admit an agent to the ward. The watcher reads
    /// the ledger, so it needs an <c>NpgsqlDataSource</c> and an <c>IWardTriggerIntake</c>; without both the listener does nothing.
    /// Call it after the outcome ledger and Psych Ward registrations.
    /// </summary>
    public static IServiceCollection AddWardOutcomeListener(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new OutcomeWardWatcherOptions());
        services.AddSingleton<IOutcomeListener>(sp =>
            sp.GetService<NpgsqlDataSource>() is { } dataSource && sp.GetService<IWardTriggerIntake>() is { } intake
                ? new WardOutcomeListener(new OutcomeWardWatcher(dataSource, intake, sp.GetRequiredService<OutcomeWardWatcherOptions>()))
                : new NoOpOutcomeListener());
        return services;
    }

    private sealed class NoOpOutcomeListener : IOutcomeListener
    {
        public Task OnRecordedAsync(Guid outcomeId, OutcomeRecord resolved, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
