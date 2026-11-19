using Bazaar.Contracts.Numerics;

namespace Bazaar.Server.Security.Budgets;

/// <summary>
/// Operator settings for per-agent token quotas (doc 48 section 11.1, doc 15 quota table). Each value is the number of
/// tokens one agent may spend in that window, as an exact <see cref="TokenAmount"/> (doc 48, DECIDED 11.3), plus the large
/// amount settings of doc 48, DECIDED 11.4.
/// </summary>
/// <remarks>
/// The shipped defaults are provisional: section 11.1 is OPEN, so these values are placeholders the operator can change in
/// settings. Every limit must be greater than zero; fractions are allowed. Values are bound from configuration (text such as
/// <c>"500"</c>, <c>"2.5e6"</c> or <c>"0.5"</c>) under <see cref="AmountParseBounds"/>, and checked by <see cref="Validate"/>.
/// <para>
/// A limit must fit <see cref="InMemoryMaxDigits"/>: limits stored as large amounts are PLANNED after the storage tier (doc
/// 48, DECIDED 11.4, "Limits as large amounts"), so today a limit is always an in-memory amount.
/// </para>
/// </remarks>
public sealed class TokenBudgetOptions
{
    /// <summary>Default of <see cref="InMemoryMaxDigits"/>: the inline tier, 131,072 digits before the point plus 16,383 after.</summary>
    public const int DefaultInMemoryMaxDigits = 147_455;

    /// <summary>Default of <see cref="LargeAmountStagingHours"/>.</summary>
    public const int DefaultLargeAmountStagingHours = 24;

    /// <summary>Tokens per agent per trailing hour. Provisional default.</summary>
    public TokenAmount HourlyTokens { get; set; } = TokenAmount.FromInt64(200_000);

    /// <summary>Tokens per agent per trailing 24 hours. Provisional default.</summary>
    public TokenAmount DailyTokens { get; set; } = TokenAmount.FromInt64(2_000_000);

    /// <summary>Tokens per agent per trailing seven days. Provisional default.</summary>
    public TokenAmount WeeklyTokens { get; set; } = TokenAmount.FromInt64(10_000_000);

    /// <summary>
    /// <c>agent.budget.amount_intake_max_digits</c> (doc 48, DECIDED 11.4): how many digits an amount may have before and
    /// after the point when it enters the server. Text longer than the guard allows is refused before it is scanned, never
    /// truncated. Default: the inline tier (<see cref="TokenAmountBounds.PostgresNumeric"/>). The operator may lower it or
    /// raise it up to <see cref="TokenAmountBounds.Unlimited"/>; a parse into memory is further bounded by
    /// <see cref="InMemoryMaxDigits"/>.
    /// </summary>
    public TokenAmountBounds AmountIntakeMaxDigits { get; set; } = TokenAmountBounds.PostgresNumeric;

    /// <summary>
    /// <c>agent.budget.in_memory_max_digits</c> (doc 48, DECIDED 11.4): the most digits, before and after the point together,
    /// that the server materialises as one in-memory <see cref="TokenAmount"/>. Above it only the streamed forms are used.
    /// Default <see cref="DefaultInMemoryMaxDigits"/>. At least 1.
    /// </summary>
    public int InMemoryMaxDigits { get; set; } = DefaultInMemoryMaxDigits;

    /// <summary>
    /// <c>agent.budget.large_amount_staging_hours</c> (doc 48, DECIDED 11.4): a large amount staged but never sealed is
    /// removed by the sweeper once it is older than this. Default <see cref="DefaultLargeAmountStagingHours"/>. At least 1.
    /// </summary>
    public int LargeAmountStagingHours { get; set; } = DefaultLargeAmountStagingHours;

    /// <summary>
    /// The bounds a budget amount is parsed under: the intake guard, tightened on each side to the in-memory maximum, so that
    /// no text (an exponent included) can make the server build an amount larger than it may hold.
    /// </summary>
    public TokenAmountBounds AmountParseBounds
    {
        get
        {
            var inMemory = Math.Max(InMemoryMaxDigits, 0);
            return TokenAmountBounds.Intersect(AmountIntakeMaxDigits, new TokenAmountBounds(inMemory, inMemory));
        }
    }

    /// <summary>The age after which an unsealed staged large amount is swept.</summary>
    public TimeSpan LargeAmountStagingAge => TimeSpan.FromHours(LargeAmountStagingHours);

    /// <summary>The configured limit for one window.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    public TokenAmount LimitFor(TokenBudgetWindow window) => window switch
    {
        TokenBudgetWindow.Hour => HourlyTokens,
        TokenBudgetWindow.Day => DailyTokens,
        TokenBudgetWindow.Week => WeeklyTokens,
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown token budget window."),
    };

    /// <summary>
    /// Checks that every window has a limit greater than zero that fits <see cref="InMemoryMaxDigits"/>, and that the large
    /// amount settings are at least 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A window limit is zero or has more digits than the in-memory maximum, or a
    /// large amount setting is below 1.</exception>
    public void Validate()
    {
        if (InMemoryMaxDigits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(InMemoryMaxDigits), InMemoryMaxDigits, "The in-memory digit maximum must be at least 1.");
        }

        if (LargeAmountStagingHours < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(LargeAmountStagingHours), LargeAmountStagingHours, "The large amount staging age must be at least one hour.");
        }

        ValidateLimit(HourlyTokens, nameof(HourlyTokens), "hourly");
        ValidateLimit(DailyTokens, nameof(DailyTokens), "daily");
        ValidateLimit(WeeklyTokens, nameof(WeeklyTokens), "weekly");
    }

    private void ValidateLimit(TokenAmount limit, string name, string label)
    {
        if (limit.IsZero)
        {
            throw new ArgumentOutOfRangeException(name, limit, $"The {label} token budget must be greater than zero.");
        }

        if (!TokenAmountDigitCounts.Of(limit).TotalAtMost(InMemoryMaxDigits))
        {
            throw new ArgumentOutOfRangeException(
                name,
                TokenAmountDigitCounts.Of(limit).ToString(),
                $"The {label} token budget has more digits than agent.budget.in_memory_max_digits ({InMemoryMaxDigits}); "
                + "limits stored as large amounts are not built yet.");
        }
    }
}
