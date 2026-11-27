namespace Bazaar.Client.OfflineSync;

/// <summary>Whether the queue accepted a write.</summary>
public enum EnqueueStatus
{
    /// <summary>The write is durably appended.</summary>
    Accepted = 1,

    /// <summary>A limit is reached; nothing was written. The caller shows the reason to the person.</summary>
    Full = 2,
}

/// <summary>The result of <see cref="EncryptedOfflineQueue.Enqueue"/>.</summary>
/// <param name="Status">Accepted or Full.</param>
/// <param name="Reason">Why the write was refused; null when accepted.</param>
public sealed record EnqueueResult(EnqueueStatus Status, string? Reason = null)
{
    /// <summary>The result for an accepted write.</summary>
    public static EnqueueResult Accepted { get; } = new(EnqueueStatus.Accepted);

    /// <summary>The result for a write refused because a limit is reached.</summary>
    /// <param name="reason">The text the person sees.</param>
    public static EnqueueResult Full(string reason) => new(EnqueueStatus.Full, reason);
}
