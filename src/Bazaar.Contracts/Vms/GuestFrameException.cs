namespace Bazaar.Contracts.Vms;

/// <summary>
/// A guest frame could not be written or read: unknown version or type, oversized, truncated, or a body that
/// does not match its type. The channel must be closed after this; the stream position is no longer usable.
/// </summary>
public sealed class GuestFrameException : Exception
{
    /// <summary>Creates the exception.</summary>
    public GuestFrameException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with the error that caused it.</summary>
    public GuestFrameException(string message, Exception inner) : base(message, inner)
    {
    }
}
