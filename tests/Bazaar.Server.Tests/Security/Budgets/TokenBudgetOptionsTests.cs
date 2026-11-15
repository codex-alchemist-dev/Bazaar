using Bazaar.Server.Security.Budgets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Tests.Security.Budgets;

public class TokenBudgetOptionsTests
{
    [Fact]
    public void Shipped_defaults_are_the_provisional_values_and_validate()
    {
        var options = new TokenBudgetOptions();

        options.Validate();
        Assert.Equal(200_000, options.LimitFor(TokenBudgetWindow.Hour));
        Assert.Equal(2_000_000, options.LimitFor(TokenBudgetWindow.Day));
        Assert.Equal(10_000_000, options.LimitFor(TokenBudgetWindow.Week));
    }

    [Theory]
    [InlineData(0, 1, 1, nameof(TokenBudgetOptions.HourlyTokens))]
    [InlineData(1, 0, 1, nameof(TokenBudgetOptions.DailyTokens))]
    [InlineData(1, 1, 0, nameof(TokenBudgetOptions.WeeklyTokens))]
    [InlineData(-5, 1, 1, nameof(TokenBudgetOptions.HourlyTokens))]
    public void Validate_rejects_a_window_limit_below_one(long hour, long day, long week, string paramName)
    {
        var options = new TokenBudgetOptions { HourlyTokens = hour, DailyTokens = day, WeeklyTokens = week };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal(paramName, error.ParamName);
    }

    [Fact]
    public void Validate_accepts_the_minimum_limit_of_one()
    {
        var options = new TokenBudgetOptions { HourlyTokens = 1, DailyTokens = 1, WeeklyTokens = 1 };

        options.Validate();
    }

    [Fact]
    public void AddAgentTokenBudgets_binds_configured_limits_and_registers_the_service()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HourlyTokens"] = "500",
                ["DailyTokens"] = "5000",
                ["WeeklyTokens"] = "50000",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<TokenBudgetOptions>();
        Assert.Equal(500, options.HourlyTokens);
        Assert.Equal(5000, options.DailyTokens);
        Assert.Equal(50000, options.WeeklyTokens);
        Assert.IsType<InMemoryTokenBudgetService>(provider.GetRequiredService<ITokenBudgetService>());
    }

    [Fact]
    public void AddAgentTokenBudgets_rejects_a_configured_limit_below_one()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HourlyTokens"] = "0" })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddAgentTokenBudgets(configuration));
    }
}
