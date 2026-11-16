using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Security.Budgets;

public class TokenBudgetServiceCollectionExtensionsTests
{
    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Safety:Budgets:HourlyTokens"] = "100" })
            .Build();

    [Fact]
    public void The_in_memory_store_is_the_default_when_no_durable_store_is_registered()
    {
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<InMemoryTokenBudgetStore>(provider.GetRequiredService<ITokenBudgetStore>());
        Assert.IsType<TokenBudgetService>(provider.GetRequiredService<ITokenBudgetService>());
    }

    [Fact]
    public void A_durable_store_registered_first_replaces_the_default()
    {
        var durable = new InMemoryTokenBudgetStore();
        var services = new ServiceCollection();
        services.AddSingleton<ITokenBudgetStore>(durable);

        services.AddAgentTokenBudgets(Configuration());
        using var provider = services.BuildServiceProvider();

        Assert.Same(durable, provider.GetRequiredService<ITokenBudgetStore>());
    }
}
