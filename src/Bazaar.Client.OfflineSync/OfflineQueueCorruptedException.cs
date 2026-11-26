namespace Bazaar.Client.OfflineSync;

/// <summary>
/// The offline queue failed verification (tamper, reorder, truncation, wrong key). The queue is stopped: it never skips a record,
/// and every later call on the same instance throws this exception again.
/// </summary>
public sealed class OfflineQueueCorruptedException : Exception
{
    /// <summary>Creates the exception with a reason.</summary>
    /// <param name="message">What failed verification.</param>
    public OfflineQueueCorruptedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a reason and the underlying failure.</summary>
    /// <param name="message">What failed verification.</param>
    /// <param name="inner">The earlier failure that stopped the queue.</param>
    public OfflineQueueCorruptedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
