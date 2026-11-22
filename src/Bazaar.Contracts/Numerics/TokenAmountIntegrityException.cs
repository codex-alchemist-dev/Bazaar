namespace Bazaar.Contracts.Numerics;

/// <summary>
/// A stored large token amount is not what was written (doc 48, DECIDED 11.4, "Integrity"): its SHA-256 does not match the
/// packed digits, a chunk is missing or has the wrong length, a byte is not a base-100 digit pair, or the digits are not in
/// canonical form for the header counts. It is a data integrity finding; a budget check that meets one denies the call.
/// </summary>
public sealed class TokenAmountIntegrityException : Exception
{
    public TokenAmountIntegrityException()
        : base("A stored token amount failed its integrity check.")
    {
    }

    public TokenAmountIntegrityException(string message)
        : base(message)
    {
    }

    public TokenAmountIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
