namespace Bazaar.Server.Identity.Totp;

/// <summary>The HMAC hash used by <see cref="TotpGenerator"/> (RFC 6238 section 1.2).</summary>
public enum TotpAlgorithm
{
    /// <summary>HMAC-SHA1, the default for most services.</summary>
    Sha1 = 0,

    /// <summary>HMAC-SHA256.</summary>
    Sha256 = 1,

    /// <summary>HMAC-SHA512.</summary>
    Sha512 = 2,
}
