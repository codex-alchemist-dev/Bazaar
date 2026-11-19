namespace Bazaar.Contracts.Numerics;

/// <summary>Why a streamed token amount was refused at intake (doc 48, DECIDED 11.4, "Transport above the frame size").</summary>
public enum TokenAmountIntakeFailure
{
    /// <summary>The text is not a plain decimal: a byte that is not a digit or a single <c>.</c>, or no digit on a side of the point.</summary>
    Malformed,

    /// <summary>The digits before or after the point exceed <c>agent.budget.amount_intake_max_digits</c>.</summary>
    OverGuard,

    /// <summary>The amount is zero or fits the inline tier, so a store of large amounts does not take it (one value, one representation).</summary>
    FitsInline,
}

/// <summary>
/// A digit stream was refused while it was read (doc 48, DECIDED 11.4): it is malformed, or it has more digits than the intake
/// guard allows. Nothing was completed; the amount is never truncated.
/// </summary>
public sealed class TokenAmountIntakeException : FormatException
{
    public TokenAmountIntakeException()
        : this(TokenAmountIntakeFailure.Malformed, "The token amount text was refused.")
    {
    }

    public TokenAmountIntakeException(string message)
        : this(TokenAmountIntakeFailure.Malformed, message)
    {
    }

    public TokenAmountIntakeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failure = TokenAmountIntakeFailure.Malformed;
    }

    public TokenAmountIntakeException(TokenAmountIntakeFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    /// <summary>Why the stream was refused.</summary>
    public TokenAmountIntakeFailure Failure { get; }
}
