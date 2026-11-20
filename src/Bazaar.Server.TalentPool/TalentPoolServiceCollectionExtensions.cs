using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.TalentPool;

public static class TalentPoolServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres template store, the Talent Pool service and the hire gate (all TryAdd, so adapters registered first win):
    /// <see cref="CouncilHireGate"/> once an <see cref="ICouncilHireMotions"/> and an <see cref="ITalentHireOutcomeStore"/> are registered,
    /// otherwise the fail-closed <see cref="DenyAllTalentHireGate"/>. The host must register an <c>NpgsqlDataSource</c> and an <see cref="ITalentModelWhitelist"/>.
    /// </summary>
    public static IServiceCollection AddBazaarTalentPool(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ITalentTemplateStore, PostgresTalentTemplateStore>();
        services.TryAddSingleton<ITalentAuditSink, NullTalentAuditSink>();
        // The Council gate needs both a motion port and an outcome store; until both are registered the gate stays fail-closed.
        services.TryAddSingleton<ITalentHireGate>(sp =>
            sp.GetService<ICouncilHireMotions>() is not null && sp.GetService<ITalentHireOutcomeStore>() is { } outcomes
                ? new CouncilHireGate(outcomes)
                : new DenyAllTalentHireGate());
        services.TryAddSingleton<TalentHireOutcomeHandler>();
        services.TryAddSingleton<ITalentPool, TalentPoolService>();
        return services;
    }
}
