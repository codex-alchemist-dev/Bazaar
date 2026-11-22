namespace Bazaar.Server.ColumnSeal;

/// <summary>Why a seal, open or key operation failed. Stable, so callers can branch on it.</summary>
public enum ColumnSealFailure
{
    /// <summary>A table, column, row key, scope or value failed validation.</summary>
    InvalidInput,

    /// <summary>The value does not open: wrong key, wrong place, or altered bytes. These are deliberately not told apart.</summary>
    CannotOpen,

    /// <summary>The DEK is unknown, or it could not be unwrapped by the KEK.</summary>
    KeyUnavailable,
}

/// <summary>
/// Thrown by column sealing. The message is a fixed sentence per <see cref="Failure"/>; it never contains plaintext, key bytes or
/// the row key.
/// </summary>
public sealed class ColumnSealException : Exception
{
    public ColumnSealException(ColumnSealFailure failure, string message)
        : base(message)
    {
        Failure = failure;
    }

    public ColumnSealException(ColumnSealFailure failure, string message, Exception inner)
        : base(message, inner)
    {
        Failure = failure;
    }

    /// <summary>The stable failure kind.</summary>
    public ColumnSealFailure Failure { get; }
}
