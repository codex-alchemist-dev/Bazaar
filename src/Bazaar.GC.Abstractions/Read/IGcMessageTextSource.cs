namespace Bazaar.Server.Gc.Read;

/// <summary>
/// Opens the text of one stored message version. Callers that need message text (importers, the Archive, exports) use this
/// instead of reading <c>gc_message_version</c> and holding keys themselves (doc 01 WP6: the GC never holds keys).
/// </summary>
public interface IGcMessageTextSource
{
    /// <summary>
    /// The opened text, or null when the version does not exist or its author was erased. The caller is responsible for
    /// having checked the viewer's access to the GC; this reads, it does not authorise.
    /// </summary>
    Task<string?> GetMessageTextAsync(Guid gcId, Guid messageId, int version, CancellationToken ct);
}
