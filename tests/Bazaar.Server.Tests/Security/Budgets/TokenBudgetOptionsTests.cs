using Bazaar.Contracts.Numerics;
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
        Assert.Equal(TokenAmount.FromInt64(200_000), options.LimitFor(TokenBudgetWindow.Hour));
        Assert.Equal(TokenAmount.FromInt64(2_000_000), options.LimitFor(TokenBudgetWindow.Day));
        Assert.Equal(TokenAmount.FromInt64(10_000_000), options.LimitFor(TokenBudgetWindow.Week));
    }

    [Theory]
    [InlineData("0", "1", "1", nameof(TokenBudgetOptions.HourlyTokens))]
    [InlineData("1", "0", "1", nameof(TokenBudgetOptions.DailyTokens))]
    [InlineData("1", "1", "0.000", nameof(TokenBudgetOptions.WeeklyTokens))]
    public void Validate_rejects_a_window_limit_of_zero(string hour, string day, string week, string paramName)
    {
        var options = new TokenBudgetOptions { HourlyTokens = TokenAmount.Parse(hour), DailyTokens = TokenAmount.Parse(day), WeeklyTokens = TokenAmount.Parse(week) };

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());

        Assert.Equal(paramName, error.ParamName);
    }

    [Fact]
    public void Validate_accepts_small_and_fractional_limits()
    {
        var options = new TokenBudgetOptions { HourlyTokens = TokenAmount.Parse("1e-16383"), DailyTokens = TokenAmount.Parse("0.5"), WeeklyTokens = TokenAmount.One };

        options.Validate();
    }

    [Fact]
    public void AddAgentTokenBudgets_binds_configured_limits_and_registers_the_service()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Safety:Budgets:HourlyTokens"] = "500",
                ["Bazaar:Safety:Budgets:DailyTokens"] = "5000",
                ["Bazaar:Safety:Budgets:WeeklyTokens"] = "50000",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<TokenBudgetOptions>();
        Assert.Equal(TokenAmount.FromInt64(500), options.HourlyTokens);
        Assert.Equal(TokenAmount.FromInt64(5000), options.DailyTokens);
        Assert.Equal(TokenAmount.FromInt64(50000), options.WeeklyTokens);
        Assert.IsType<TokenBudgetService>(provider.GetRequiredService<ITokenBudgetService>());
    }

    [Fact]
    public void AddAgentTokenBudgets_binds_exponent_fraction_and_beyond_long_limits()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bazaar:Safety:Budgets:HourlyTokens"] = "2.5e6",
                ["Bazaar:Safety:Budgets:DailyTokens"] = "0.75",
                ["Bazaar:Safety:Budgets:WeeklyTokens"] = "123456789012345678901234567890",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<TokenBudgetOptions>();
        Assert.Equal("2500000", options.HourlyTokens.ToString());
        Assert.Equal("0.75", options.DailyTokens.ToString());
        Assert.Equal("123456789012345678901234567890", options.WeeklyTokens.ToString());
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1_000")]
    [InlineData("NaN")]
    [InlineData("1e999999999")]
    public void AddAgentTokenBudgets_rejects_a_configured_limit_that_is_not_an_amount(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Safety:Budgets:HourlyTokens"] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddAgentTokenBudgets(configuration));
    }

    [Fact]
    public void AddAgentTokenBudgets_rejects_a_configured_limit_of_zero()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Safety:Budgets:HourlyTokens"] = "0" })
            .Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddAgentTokenBudgets(configuration));
    }

    [Fact]
    public void AddAgentTokenBudgets_ignores_root_level_keys_and_uses_the_defaults()
    {
        // Root-level keys are not the budget section, so they must not bind or be validated.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HourlyTokens"] = "0",
                ["DailyTokens"] = "7",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<TokenBudgetOptions>();
        Assert.Equal(TokenAmount.FromInt64(200_000), options.HourlyTokens);
        Assert.Equal(TokenAmount.FromInt64(2_000_000), options.DailyTokens);
    }

    [Fact]
    public void AddAgentTokenBudgets_binds_only_the_named_section_when_root_keys_are_also_present()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WeeklyTokens"] = "1",
                ["Bazaar:Safety:Budgets:WeeklyTokens"] = "42",
            })
            .Build();
        var services = new ServiceCollection();

        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(TokenAmount.FromInt64(42), provider.GetRequiredService<TokenBudgetOptions>().WeeklyTokens);
    }

    [Fact]
    public void The_configuration_section_key_is_bazaar_safety_budgets()
    {
        Assert.Equal("Bazaar:Safety:Budgets", TokenBudgetServiceCollectionExtensions.ConfigurationSection);
    }
}
