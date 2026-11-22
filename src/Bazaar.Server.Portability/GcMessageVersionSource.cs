using Bazaar.Contracts.Gc;

namespace Bazaar.Server.Portability;

/// <summary>
/// One stored version of a GC message (docs/design/01, <c>gc_message_version</c>), read for export. Only ciphertext is carried:
/// the export never decrypts a body, so there is no plaintext field here (doc 44 sections 4.6 and 10).
/// </summary>
/// <param name="Seq">The GC event seq that wrote this version. Every message or edit event has a version row at its seq.</param>
/// <param name="MessageId">The message the version belongs to.</param>
/// <param name="Version">Version number, starting at 1.</param>
/// <param name="EditorId">The principal who wrote the version.</param>
/// <param name="CreatedAt">When the version was written.</param>
/// <param name="Tombstone">True for an erased version. A tombstone has no body.</param>
/// <param name="KeyId">The key the body is sealed under. Null for a tombstone.</param>
/// <param name="Nonce">The cipher nonce. Null for a tombstone.</param>
/// <param name="Ciphertext">The sealed body bytes. Null for a tombstone.</param>
public sealed record GcMessageVersionRecord(
    long Seq,
    Guid MessageId,
    int Version,
    Guid EditorId,
    DateTimeOffset CreatedAt,
    bool Tombstone,
    Guid? KeyId,
    byte[]? Nonce,
    byte[]? Ciphertext);

/// <summary>
/// The read side the GC exporter needs for message versions (doc 44 section 4.2, <c>versions.jsonl</c>). It is not in
/// Bazaar.GC.Abstractions: <see cref="IGcLogReader"/> returns events only, and events carry no body. The host implements this
/// over <c>gc_message_version</c>.
/// </summary>
public interface IGcMessageVersionSource
{
    /// <summary>
    /// Versions whose seq is greater than <paramref name="afterSeq"/>, in ascending seq order, at most <paramref name="limit"/> of
    /// them, none past the viewer's grant. Throws <c>GcAccessDeniedException</c> when the viewer may not read the GC.
    /// </summary>
    IAsyncEnumerable<GcMessageVersionRecord> ReadAsync(Guid gcId, long afterSeq, int limit, GcPrincipal viewer, CancellationToken ct);
}
