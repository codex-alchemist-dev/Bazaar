namespace Bazaar.Client.LocalInference.Models;

/// <summary>Install and resolve policy for manifests. When <see cref="RequireSignature"/> is set, an unsigned manifest is rejected.</summary>
public sealed record ManifestPolicy(bool RequireSignature);

/// <summary>
/// Checks a manifest's detached signature. The signature scheme is not decided (doc 31 leaves the manifest signing
/// key and format open), so the platform supplies the implementation. A signature that does not verify is always rejected.
/// </summary>
public interface IManifestVerifier
{
    bool Verify(ModelManifest manifest, string signature);
}

/// <summary>
/// Default verifier until a signature scheme is chosen. It rejects every signature, so a signed manifest cannot be
/// accepted by accident.
/// </summary>
public sealed class FailClosedManifestVerifier : IManifestVerifier
{
    public static FailClosedManifestVerifier Instance { get; } = new();

    public bool Verify(ModelManifest manifest, string signature) => false;
}
