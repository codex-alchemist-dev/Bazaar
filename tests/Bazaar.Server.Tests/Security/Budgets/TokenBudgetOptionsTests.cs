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

    [Fact]
    public void Large_amount_settings_default_to_the_doc_48_table()
    {
        // Doc 48, DECIDED 11.4: intake guard and in-memory maximum default to the inline tier; staging age 24 hours.
        var options = new TokenBudgetOptions();

        Assert.Equal(TokenAmountBounds.PostgresNumeric, options.AmountIntakeMaxDigits);
        Assert.Equal(147_455, options.InMemoryMaxDigits);
        Assert.Equal(24, options.LargeAmountStagingHours);
        Assert.Equal(TimeSpan.FromHours(24), options.LargeAmountStagingAge);
        Assert.Equal(TokenAmountBounds.PostgresNumeric, options.AmountParseBounds);
    }

    [Theory]
    [InlineData(0, 24, nameof(TokenBudgetOptions.InMemoryMaxDigits))]
    [InlineData(-1, 24, nameof(TokenBudgetOptions.InMemoryMaxDigits))]
    [InlineData(10, 0, nameof(TokenBudgetOptions.LargeAmountStagingHours))]
    public void Validate_rejects_large_amount_settings_below_one(int inMemoryMaxDigits, int stagingHours, string paramName)
    {
        var options = new TokenBudgetOptions { InMemoryMaxDigits = inMemoryMaxDigits, LargeAmountStagingHours = stagingHours };

        Assert.Equal(paramName, Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate()).ParamName);
    }

    [Fact]
    public void Validate_rejects_a_limit_with_more_digits_than_the_in_memory_maximum()
    {
        // A limit given as an amount must be materialisable; a larger one is given by reference (next tests).
        var tooLong = TokenAmount.Parse("1e147455", new TokenAmountBounds(150_000, 0));
        var options = new TokenBudgetOptions { WeeklyTokens = tooLong };

        Assert.Equal(nameof(TokenBudgetOptions.WeeklyTokens), Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate()).ParamName);

        options.InMemoryMaxDigits = 147_456;
        options.Validate();
    }

    [Fact]
    public void A_raised_intake_guard_binds_a_limit_beyond_the_numeric_bounds()
    {
        var options = Bind(new()
        {
            ["AmountIntakeMaxDigits"] = "unlimited",
            ["InMemoryMaxDigits"] = "200001",
            ["LargeAmountStagingHours"] = "6",
            ["HourlyTokens"] = "1e200000",
        });

        Assert.True(options.AmountIntakeMaxDigits.IsUnlimited);
        Assert.Equal(200_001, options.InMemoryMaxDigits);
        Assert.Equal(6, options.LargeAmountStagingHours);
        Assert.Equal(200_001, options.HourlyTokens.IntegerDigitCount);
    }

    [Fact]
    public void The_default_intake_guard_refuses_a_limit_beyond_the_inline_tier()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Bind(new() { ["HourlyTokens"] = "1e131072" }));

        Assert.Contains("HourlyTokens", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlimited_intake_guard_is_still_bounded_by_the_in_memory_maximum()
    {
        // An exponent is short text for a huge value; the parse bound keeps it from being built in memory.
        Assert.Throws<InvalidOperationException>(() => Bind(new() { ["AmountIntakeMaxDigits"] = "unlimited", ["DailyTokens"] = "1e147455" }));
    }

    [Fact]
    public void Text_longer_than_a_lowered_guard_is_refused_before_it_is_read()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Bind(new() { ["AmountIntakeMaxDigits"] = "5,0", ["WeeklyTokens"] = new string('0', 40) + "1" }));

        Assert.Contains("not read", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Bind(new() { ["AmountIntakeMaxDigits"] = "5,0", ["WeeklyTokens"] = "999999" }));
        Assert.Equal(TokenAmount.FromInt64(99_999), Bind(new() { ["AmountIntakeMaxDigits"] = "5,0", ["WeeklyTokens"] = "99999" }).WeeklyTokens);
    }

    [Fact]
    public void A_limit_setting_holding_an_intake_reference_binds_as_that_window_reference()
    {
        var reference = Guid.Parse("01890000-0000-7000-8000-000000000001");

        var options = Bind(new() { ["WeeklyTokens"] = " " + reference.ToString("D") + " ", ["HourlyTokens"] = "500" });

        Assert.Equal(reference, options.WeeklyTokensReference);
        Assert.Equal(reference, options.LimitReferenceFor(TokenBudgetWindow.Week));
        Assert.Null(options.HourlyTokensReference);
        Assert.Null(options.LimitReferenceFor(TokenBudgetWindow.Day));
        Assert.True(options.HasLargeLimits);
        Assert.Equal(TokenAmount.FromInt64(500), options.LimitFor(TokenBudgetWindow.Hour));

        // A referenced limit is never read as an in-memory amount, so no stale value can stand in for it.
        Assert.Throws<InvalidOperationException>(() => options.LimitFor(TokenBudgetWindow.Week));
        Assert.False(new TokenBudgetOptions().HasLargeLimits);
    }

    [Fact]
    public void A_limit_given_by_reference_need_not_fit_the_in_memory_maximum()
    {
        // The in-memory value of the window is ignored once a reference is set, even when it would not validate.
        var options = new TokenBudgetOptions { DailyTokens = TokenAmount.Zero, DailyTokensReference = Guid.Parse("01890000-0000-7000-8000-000000000002") };

        options.Validate();
    }

    [Theory]
    [InlineData("{01890000-0000-7000-8000-000000000001}")]
    [InlineData("0189000000007000800000000000000a")]
    [InlineData("01890000-0000-7000-8000-00000000000g")]
    public void A_reference_in_another_form_is_not_a_reference_and_not_an_amount(string value)
    {
        Assert.Throws<InvalidOperationException>(() => Bind(new() { ["DailyTokens"] = value }));
    }

    [Fact]
    public void The_shared_evaluator_denies_every_call_while_a_limit_is_a_reference()
    {
        var options = new TokenBudgetOptions { HourlyTokensReference = Guid.Parse("01890000-0000-7000-8000-000000000003") };

        var decision = TokenBudgetEvaluator.Evaluate([], DateTimeOffset.UnixEpoch, TokenAmount.FromInt64(1), options);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.MaxValue, decision.RetryAfter);
    }

    [Fact]
    public void The_fast_path_over_digit_counts_denies_a_spend_with_more_integer_digits_than_a_limit()
    {
        var limits = new Dictionary<TokenBudgetWindow, long> { [TokenBudgetWindow.Hour] = 200_001, [TokenBudgetWindow.Day] = 3, [TokenBudgetWindow.Week] = 200_001 };

        var denied = TokenBudgetEvaluator.RejectOversized(TokenAmount.FromInt64(1_000), window => limits[window]);

        Assert.NotNull(denied);
        Assert.Equal(TokenBudgetWindow.Day, denied.Window);
        Assert.Null(TokenBudgetEvaluator.RejectOversized(TokenAmount.FromInt64(999), window => limits[window]));
    }

    [Theory]
    [InlineData("AmountIntakeMaxDigits", "lots")]
    [InlineData("AmountIntakeMaxDigits", "-1,0")]
    [InlineData("InMemoryMaxDigits", "many")]
    [InlineData("LargeAmountStagingHours", "1.5")]
    public void Malformed_large_amount_settings_are_refused(string key, string value)
    {
        Assert.Throws<InvalidOperationException>(() => Bind(new() { [key] = value }));
    }

    private static TokenBudgetOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => $"Bazaar:Safety:Budgets:{pair.Key}", pair => pair.Value))
            .Build();
        var services = new ServiceCollection();
        services.AddAgentTokenBudgets(configuration);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<TokenBudgetOptions>();
    }
}
