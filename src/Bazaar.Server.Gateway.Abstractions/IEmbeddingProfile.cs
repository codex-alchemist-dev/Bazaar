namespace Bazaar.Server.Gateway;

/// <summary>
/// The install's active embedding profile as the gateway needs it: the vector length every stored embedding must have
/// (doc 05 "Dimension check"; the <c>halfvec</c> dimension of C39). The host implements it from the existing
/// <c>embedding_profile</c> table; the gateway never stores a profile of its own.
/// </summary>
public interface IEmbeddingProfile
{
    /// <summary>The active profile's dimension, or null when no profile is registered (the gateway then skips the check).</summary>
    ValueTask<int?> GetDimensionsAsync(CancellationToken cancellationToken);
}

/// <summary>The default: no profile is registered, so the gateway does not check vector lengths.</summary>
public sealed class NullEmbeddingProfile : IEmbeddingProfile
{
    public static NullEmbeddingProfile Instance { get; } = new();

    public ValueTask<int?> GetDimensionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<int?>(null);
}
