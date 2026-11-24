namespace Bazaar.Server.Memory.Write;

/// <summary>Commits a memory batch to the Vault. The real writer implements this; tests use fakes.</summary>
public interface IMemoryBatchSink
{
    /// <summary>Writes every item in the batch and makes one commit with the batch trailers.</summary>
    Task<MemoryCommitResult> CommitAsync(MemoryBatch batch, CancellationToken ct);
}
