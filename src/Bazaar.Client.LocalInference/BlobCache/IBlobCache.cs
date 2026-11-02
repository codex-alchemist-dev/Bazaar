namespace Bazaar.Client.LocalInference.BlobCache;

/// <summary>
/// Local cache of compiled NPU blobs. Blobs are never downloaded and never shared between machines. A lookup returns a
/// path only when the stored key matches the requested key exactly; anything else is a miss and the caller recompiles.
/// </summary>
public interface IBlobCache
{
    /// <summary>Returns the blob path for <paramref name="key"/>, or null when there is no blob or its recorded key differs.</summary>
    string? TryGet(BlobCacheKey key);

    /// <summary>Copies <paramref name="sourcePath"/> into the cache under <paramref name="key"/> and returns the stored path. Replaces any entry with the same key.</summary>
    string Put(BlobCacheKey key, string sourcePath);

    /// <summary>Deletes the entry for <paramref name="key"/>. Returns false when there was none.</summary>
    bool Remove(BlobCacheKey key);

    /// <summary>Deletes every entry for the model of <paramref name="current"/> whose key is not <paramref name="current"/>. Other models are untouched. Returns the number of entries removed.</summary>
    int Prune(BlobCacheKey current);
}
