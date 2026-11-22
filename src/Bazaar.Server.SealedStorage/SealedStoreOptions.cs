namespace Bazaar.Server.SealedStorage;

/// <summary>Where sealed scopes live. The folder holds only ciphertext; a plaintext file found in it is never read.</summary>
public sealed class SealedStoreOptions
{
    /// <summary>The root folder of all sealed scopes (default <c>&lt;data root&gt;/sealed</c> when registered by the component).</summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// The largest total, in bytes, of one <see cref="ISealedScope.ReadAllAsync"/> snapshot or one <see cref="ISealedScope.WriteBatchAsync"/> batch,
    /// both of which hold their files in memory (default 1 GiB). Each single file stays limited to 256 MiB.
    /// </summary>
    public long MaxBulkBytes { get; init; } = 1L * 1024 * 1024 * 1024;
}
