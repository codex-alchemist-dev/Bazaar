using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Security.Budgets;

public static class TokenBudgetServiceCollectionExtensions
{
    // The section key is a proposal until the settings registry decides it. Changing it moves every operator's budget settings.
    /// <summary>Configuration section that holds <see cref="TokenBudgetOptions"/>. Proposed; not yet decided by the settings registry.</summary>
    public const string ConfigurationSection = "Bazaar:Safety:Budgets";

    /// <summary>
    /// Binds <see cref="TokenBudgetOptions"/> from the <see cref="ConfigurationSection"/> section of
    /// <paramref name="configuration"/>, validates them, and registers the token budget service. Keys at the root of
    /// <paramref name="configuration"/> are not read. Uses <see cref="TimeProvider.System"/> unless the host already
    /// registered one. Uses <see cref="InMemoryTokenBudgetStore"/> unless the host registered an <see cref="ITokenBudgetStore"/>
    /// first; register a durable store before calling this so the counters survive a restart.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A bound window limit is zero.</exception>
    /// <exception cref="InvalidOperationException">A configured limit is not a token amount (for example negative or malformed).</exception>
    // TODO(component): call from host composition when the component model (PR #125) merges. Not called yet, like AddBazaarRateLimits.
    public static IServiceCollection AddAgentTokenBudgets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(ConfigurationSection).Get<TokenBudgetOptions>() ?? new TokenBudgetOptions();
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITokenBudgetStore, InMemoryTokenBudgetStore>();
        services.TryAddSingleton<ITokenBudgetService, TokenBudgetService>();
        return services;
    }
}
