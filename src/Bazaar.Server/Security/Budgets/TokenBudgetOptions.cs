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
/// A limit given as an amount must fit <see cref="InMemoryMaxDigits"/>. A larger limit is a large amount sealed through the
/// digit-stream intake, and the window's setting holds its reference (<see cref="HourlyTokensReference"/> and the others; doc
/// 48, DECIDED 11.4, "Limits as large amounts"). Only the Postgres budget store can read a reference; it compares the streamed
/// window sum with the stored limit and never materialises either.
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
    /// The hourly limit as a reference to a sealed intake amount in the large tier; when set, it replaces
    /// <see cref="HourlyTokens"/> (doc 48, DECIDED 11.4, "Limits as large amounts").
    /// </summary>
    public Guid? HourlyTokensReference { get; set; }

    /// <summary>The daily limit as a reference to a sealed intake amount; when set, it replaces <see cref="DailyTokens"/>.</summary>
    public Guid? DailyTokensReference { get; set; }

    /// <summary>The weekly limit as a reference to a sealed intake amount; when set, it replaces <see cref="WeeklyTokens"/>.</summary>
    public Guid? WeeklyTokensReference { get; set; }

    /// <summary>True when any window limit is a reference to a large amount.</summary>
    public bool HasLargeLimits => HourlyTokensReference is not null || DailyTokensReference is not null || WeeklyTokensReference is not null;

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

    /// <summary>The configured limit for one window, when it is an in-memory amount.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    /// <exception cref="InvalidOperationException">The window's limit is a reference (<see cref="LimitReferenceFor"/>), which
    /// only a store that reads the large tier can use.</exception>
    public TokenAmount LimitFor(TokenBudgetWindow window)
    {
        if (LimitReferenceFor(window) is { } reference)
        {
            throw new InvalidOperationException($"The {window} token budget is the large amount {reference}; it is not held in memory.");
        }

        return window switch
        {
            TokenBudgetWindow.Hour => HourlyTokens,
            TokenBudgetWindow.Day => DailyTokens,
            _ => WeeklyTokens,
        };
    }

    /// <summary>The reference to the sealed large amount that is this window's limit; null when the limit is in memory.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="window"/> is not a defined value.</exception>
    public Guid? LimitReferenceFor(TokenBudgetWindow window) => window switch
    {
        TokenBudgetWindow.Hour => HourlyTokensReference,
        TokenBudgetWindow.Day => DailyTokensReference,
        TokenBudgetWindow.Week => WeeklyTokensReference,
        _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown token budget window."),
    };

    /// <summary>
    /// Checks that every window given as an amount has a limit greater than zero that fits <see cref="InMemoryMaxDigits"/>,
    /// and that the large amount settings are at least 1. A window given by reference is checked by the store that reads it;
    /// the intake seals only amounts beyond the inline tier, so a sealed intake amount is never zero.
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

        ValidateLimit(HourlyTokens, HourlyTokensReference, nameof(HourlyTokens), "hourly");
        ValidateLimit(DailyTokens, DailyTokensReference, nameof(DailyTokens), "daily");
        ValidateLimit(WeeklyTokens, WeeklyTokensReference, nameof(WeeklyTokens), "weekly");
    }

    private void ValidateLimit(TokenAmount limit, Guid? reference, string name, string label)
    {
        if (reference is not null)
        {
            return;
        }

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
                + "set a larger limit by reference to an intake amount.");
        }
    }
}
