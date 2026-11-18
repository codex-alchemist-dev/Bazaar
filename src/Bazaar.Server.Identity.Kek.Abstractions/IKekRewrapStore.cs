namespace Bazaar.Server.Identity.Kek;

/// <summary>One live wrapped data key, with the associated data it was wrapped under.</summary>
public sealed record KekWrapRecord(Guid Id, byte[] Wrapped, byte[] AssociatedData);

/// <summary>
/// The rows a KEK re-wrap walks (doc 14 rotation: re-wrap DEKs, never re-encrypt rows). Implementations are keyset-paged by id
/// and replace a wrap only when the stored bytes still equal the expected bytes (compare-and-set).
/// </summary>
public interface IKekRewrapStore
{
    /// <summary>Live rows (not destroyed) with id greater than <paramref name="afterId"/>, ordered by id, at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<KekWrapRecord>> ListLiveWrapsAsync(Guid? afterId, int limit, CancellationToken ct);

    /// <summary>
    /// Stores <paramref name="replacement"/> only if the row still holds <paramref name="expected"/>, and records <paramref name="kekId"/>
    /// as the KEK that now wraps it where the table keeps one (<c>soul_dek.kek_id</c>). Returns false when the row changed.
    /// </summary>
    Task<bool> ReplaceWrapAsync(Guid id, ReadOnlyMemory<byte> expected, ReadOnlyMemory<byte> replacement, string kekId, CancellationToken ct);
}

/// <summary>Receives re-wrap audit events (doc 15 <c>audit_log</c>). The host adapts its audit log to this seam.</summary>
public interface IKekAuditSink
{
    Task RecordAsync(string kind, string payloadJson, CancellationToken ct);
}
