namespace Bazaar.Server.Gc.Write;

/// <summary>The only way events enter a GC log (docs/design/01, <c>AppendAsync</c>).</summary>
public interface IGcLogWriter
{
    /// <summary>
    /// Appends one event. Idempotent by <c>(GcId, AuthorId, ClientMsgId)</c>: a repeat returns the original event and writes
    /// nothing. Throws <see cref="GcNotFoundException"/> or <see cref="GcArchivedException"/>; any other failure rolls back
    /// the whole append, seq included, so a failed append leaves no gap.
    /// </summary>
    Task<AppendResult> AppendAsync(AppendRequest request, CancellationToken ct);

    /// <summary>
    /// Appends several events to one GC in a single transaction, with consecutive seqs, in order. Bulk and system writers use
    /// this because one hot GC takes about 400 to 700 appends a second (spike S3). Duplicates are answered with the original
    /// event and the rest are appended. Keys must be distinct within the batch.
    /// </summary>
    Task<IReadOnlyList<AppendResult>> AppendManyAsync(IReadOnlyList<AppendRequest> requests, CancellationToken ct);
}
