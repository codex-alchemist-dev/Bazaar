namespace Bazaar.Server.SealedStorage;

/// <summary>Where sealed scopes live. The folder holds only ciphertext; a plaintext file found in it is never read.</summary>
public sealed class SealedStoreOptions
{
    /// <summary>The root folder of all sealed scopes (default <c>&lt;data root&gt;/sealed</c> when registered by the component).</summary>
    public required string RootPath { get; init; }
}
