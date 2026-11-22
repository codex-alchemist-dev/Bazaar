namespace Bazaar.Server.Security.Readers;

/// <summary>
/// Scratch space for a reader session. Items are keyed by session id and purged completely when the session ends. Readers
/// use it to hold notes while they work; it is never delivered to an actor.
/// </summary>
public interface IReaderScratchStore
{
    /// <summary>Stores an item for a live session, replacing any item with the same key.</summary>
    /// <exception cref="InvalidOperationException">The session has been purged.</exception>
    Task PutAsync(Guid sessionId, string key, string value, CancellationToken ct);

    /// <summary>Reads an item, or null when it is absent.</summary>
    Task<string?> GetAsync(Guid sessionId, string key, CancellationToken ct);

    /// <summary>Removes every item of the session and refuses later writes to it. Safe to call more than once.</summary>
    Task PurgeAsync(Guid sessionId, CancellationToken ct);
}
