namespace Bazaar.Identity.Kek;

/// <summary>One live data key row: its id, the wrap, and the id of the KEK that wrapped it.</summary>
public sealed record SoulDekRecord(Guid DekId, byte[] Wrapped, string KekId);

/// <summary>
/// Storage for per-scope data keys (doc 14 <c>soul_dek</c>). A scope is a Soul id as text, or <c>install</c> for provider and App keys.
/// There is at most one live DEK per scope. Crypto-shred retires the row and clears the wrap, which makes every ciphertext under
/// that DEK unreadable.
/// </summary>
public interface ISoulDekStore
{
    /// <summary>The live row for the scope, or null when the scope has none (never created, or shredded).</summary>
    Task<SoulDekRecord?> GetLiveAsync(string scope, CancellationToken ct);

    /// <summary>Inserts a live DEK. Returns false when the scope already has a live one (the caller reads it instead).</summary>
    Task<bool> TryInsertLiveAsync(Guid dekId, string scope, byte[] wrapped, string kekId, CancellationToken ct);

    /// <summary>Retires the scope's live DEK and clears its wrap. Returns the retired id, or null when there was none.</summary>
    Task<Guid?> ShredAsync(string scope, CancellationToken ct);
}
