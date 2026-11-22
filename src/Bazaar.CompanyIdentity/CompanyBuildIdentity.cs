namespace Bazaar.CompanyIdentity;

/// <summary>
/// The company server this client build was made for (doc 37 section 4a). A company build replaces this file with a generated
/// version that carries the server's address and key-hash pin. This source file is the unset identity, so a release head built
/// from source has no server and fails closed with a clear message (ClientServer.ConnectToCompanyServer).
/// </summary>
public static class CompanyBuildIdentity
{
    /// <summary>The server's base address, or null when this build carries no identity.</summary>
    public static Uri? Address => null;

    /// <summary>The SHA-256 pin of the server's key, or null when this build carries no identity.</summary>
    public static string? KeyHashPin => null;

    /// <summary>Lowercase hex fingerprint of the update signing key, or null when this build carries no update trust.</summary>
    public static string? UpdateSigningKeyFingerprint => null;

    /// <summary>The ASCII-armoured OpenPGP public key text for release manifests, or null when this build carries no update trust.</summary>
    public static string? UpdateSigningPublicKey => null;

    /// <summary>True only for the owner's own build (allows the Latest-Commit channel). False when unset.</summary>
    public static bool IsOwnerInstall => false;

    /// <summary>The release manifest address on the company server's origin, or null when this build carries no update trust.</summary>
    public static Uri? ReleaseManifestUrl => null;
}
