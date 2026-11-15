using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security.Budgets;

public static class TokenBudgetServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="TokenBudgetOptions"/> from <paramref name="configuration"/>, validates them, and registers the
    /// token budget service. Uses <see cref="TimeProvider.System"/> unless the host already registered one. Uses
    /// <see cref="InMemoryTokenBudgetStore"/> unless the host registered an <see cref="ITokenBudgetStore"/> first; register
    /// a durable store before calling this so the counters survive a restart.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound window limit is below 1.</exception>
    // TODO(component): call from host composition when the component model (PR #125) merges. Not called yet, like AddBazaarRateLimits.
    public static IServiceCollection AddAgentTokenBudgets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.Get<TokenBudgetOptions>() ?? new TokenBudgetOptions();
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITokenBudgetStore, InMemoryTokenBudgetStore>();
        services.TryAddSingleton<ITokenBudgetService, TokenBudgetService>();
        return services;
    }
}
