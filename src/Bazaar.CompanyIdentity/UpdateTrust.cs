namespace Bazaar.CompanyIdentity;

/// <summary>
/// The update-trust values a company build carries for the client updater (doc 37 section 4c): the fingerprint and public key
/// that release manifests must be signed with, the manifest address, and the owner-install flag. Built only by
/// <see cref="Create"/>, which fails closed on anything malformed, the way <see cref="CompanyServerIdentity"/> does for the pin.
/// </summary>
public sealed record UpdateTrust
{
    /// <summary>The armour header every OpenPGP public key block starts with.</summary>
    public const string ArmourHeader = "-----BEGIN PGP PUBLIC KEY BLOCK-----";

    private UpdateTrust(string signingKeyFingerprint, string signingPublicKey, Uri releaseManifestUrl, bool isOwnerInstall)
    {
        UpdateSigningKeyFingerprint = signingKeyFingerprint;
        UpdateSigningPublicKey = signingPublicKey;
        ReleaseManifestUrl = releaseManifestUrl;
        IsOwnerInstall = isOwnerInstall;
    }

    /// <summary>Lowercase hex fingerprint of the update signing key (40 or 64 characters).</summary>
    public string UpdateSigningKeyFingerprint { get; }

    /// <summary>The ASCII-armoured OpenPGP public key text that verifies release manifests.</summary>
    public string UpdateSigningPublicKey { get; }

    /// <summary>The release manifest address: https, on the company server's own origin.</summary>
    public Uri ReleaseManifestUrl { get; }

    /// <summary>True only for the owner's own build; it allows the Latest-Commit channel. Everyone else's build is false.</summary>
    public bool IsOwnerInstall { get; }

    /// <summary>
    /// Validates the values and builds the trust. The fingerprint rule matches <c>ReleaseManifest.SigningKeyFingerprint</c>
    /// (<c>Bazaar.Contracts/Releases/ReleaseValidation.cs</c>, PR #941): lowercase hex, 40 or 64 characters.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not acceptable.</exception>
    public static UpdateTrust Create(string signingKeyFingerprint, string signingPublicKey, Uri releaseManifestUrl, bool isOwnerInstall = false)
    {
        ArgumentNullException.ThrowIfNull(signingKeyFingerprint);
        ArgumentNullException.ThrowIfNull(signingPublicKey);
        ArgumentNullException.ThrowIfNull(releaseManifestUrl);

        if (!IsWellFormedFingerprint(signingKeyFingerprint))
            throw new ArgumentException("The update signing key fingerprint must be 40 or 64 lowercase hex characters.", nameof(signingKeyFingerprint));

        var key = signingPublicKey.Trim();
        if (key.Length == 0)
            throw new ArgumentException("The update signing public key must not be empty.", nameof(signingPublicKey));
        if (!key.StartsWith(ArmourHeader, StringComparison.Ordinal))
            throw new ArgumentException($"The update signing public key must be ASCII-armoured and start with \"{ArmourHeader}\".", nameof(signingPublicKey));

        if (!releaseManifestUrl.IsAbsoluteUri)
            throw new ArgumentException("The release manifest URL must be absolute.", nameof(releaseManifestUrl));
        if (releaseManifestUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The release manifest URL must use https.", nameof(releaseManifestUrl));
        if (!string.IsNullOrEmpty(releaseManifestUrl.UserInfo))
            throw new ArgumentException("The release manifest URL must not carry user information.", nameof(releaseManifestUrl));

        return new UpdateTrust(signingKeyFingerprint, key, releaseManifestUrl, isOwnerInstall);
    }

    /// <summary>True when <paramref name="value"/> is lowercase hex of 40 or 64 characters.</summary>
    public static bool IsWellFormedFingerprint(string? value)
    {
        if (value is null || (value.Length != 40 && value.Length != 64)) return false;
        foreach (var c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    /// <summary>True when both addresses share scheme, host and port.</summary>
    internal static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.Ordinal)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}
