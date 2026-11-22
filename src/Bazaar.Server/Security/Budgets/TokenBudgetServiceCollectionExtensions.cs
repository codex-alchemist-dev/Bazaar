using System.Globalization;
using Bazaar.Contracts.Numerics;
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
    /// <exception cref="ArgumentOutOfRangeException">A bound window limit is zero or too large for memory, or a large amount setting is below 1.</exception>
    /// <exception cref="InvalidOperationException">A configured value is malformed, or a limit is not a token amount within the intake guard.</exception>
    // TODO(component): call from host composition when the component model (PR #125) merges. Not called yet, like AddBazaarRateLimits.
    public static IServiceCollection AddAgentTokenBudgets(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = BindOptions(configuration.GetSection(ConfigurationSection));
        options.Validate();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ITokenBudgetStore, InMemoryTokenBudgetStore>();
        services.TryAddSingleton<ITokenBudgetService, TokenBudgetService>();
        return services;
    }

    /// <summary>
    /// Reads <see cref="TokenBudgetOptions"/> from <paramref name="section"/>. The digit settings are read first, so every
    /// limit is parsed under the intake guard (doc 48, DECIDED 11.4): text longer than the guard allows is refused before it
    /// is scanned, and a value with more digits than the guard or the in-memory maximum allows is refused, never truncated.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is malformed or outside the guard.</exception>
    public static TokenBudgetOptions BindOptions(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var options = new TokenBudgetOptions();

        if (section[nameof(TokenBudgetOptions.AmountIntakeMaxDigits)] is { } guard)
        {
            options.AmountIntakeMaxDigits = TokenAmountBounds.TryParse(guard, out var bounds)
                ? bounds
                : throw Malformed(section, nameof(TokenBudgetOptions.AmountIntakeMaxDigits), "expected 'unlimited' or '<integer digits>,<fraction digits>'");
        }

        options.InMemoryMaxDigits = ReadInt(section, nameof(TokenBudgetOptions.InMemoryMaxDigits), options.InMemoryMaxDigits);
        options.LargeAmountStagingHours = ReadInt(section, nameof(TokenBudgetOptions.LargeAmountStagingHours), options.LargeAmountStagingHours);

        var parseBounds = options.AmountParseBounds;
        (options.HourlyTokens, options.HourlyTokensReference) =
            ReadLimit(section, nameof(TokenBudgetOptions.HourlyTokens), options.HourlyTokens, options.AmountIntakeMaxDigits, parseBounds);
        (options.DailyTokens, options.DailyTokensReference) =
            ReadLimit(section, nameof(TokenBudgetOptions.DailyTokens), options.DailyTokens, options.AmountIntakeMaxDigits, parseBounds);
        (options.WeeklyTokens, options.WeeklyTokensReference) =
            ReadLimit(section, nameof(TokenBudgetOptions.WeeklyTokens), options.WeeklyTokens, options.AmountIntakeMaxDigits, parseBounds);
        return options;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        section[key] is not { } text
            ? fallback
            : int.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw Malformed(section, key, "expected a whole number");

    /// <summary>
    /// One window limit: an amount in text, or the id (UUID text, <c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>) of a sealed intake
    /// amount, which is then the limit (doc 48, DECIDED 11.4, "Limits as large amounts"). The two forms cannot be confused:
    /// an amount has no <c>-</c>.
    /// </summary>
    private static (TokenAmount Amount, Guid? Reference) ReadLimit(
        IConfigurationSection section, string key, TokenAmount fallback, TokenAmountBounds guard, TokenAmountBounds parseBounds)
    {
        if (section[key] is not { } text)
        {
            return (fallback, null);
        }

        if (Guid.TryParseExact(text.AsSpan().Trim(), "D", out var reference))
        {
            return (fallback, reference);
        }

        // The guard's own text length is checked before any character is looked at, so over-long text costs nothing.
        if (text.Length > guard.MaxTextLength)
        {
            throw Malformed(section, key, $"the text has {text.Length} characters, more than agent.budget.amount_intake_max_digits ({guard}) allows; it was not read");
        }

        return TokenAmount.TryParse(text, parseBounds, out var amount)
            ? (amount, null)
            : throw Malformed(section, key, $"expected a token amount (ASCII digits, an optional fraction and exponent) within {parseBounds} digits, or the id of an intake amount");
    }

    private static InvalidOperationException Malformed(IConfigurationSection section, string key, string reason) =>
        new($"Configuration value '{section.Path}:{key}' is not valid: {reason}.");
}
