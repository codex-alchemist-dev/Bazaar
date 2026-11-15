namespace Bazaar.Server.Modules.Trust;

/// <summary>The typed outcome of verifying a module package signature. A module is installable only when it is <see cref="Valid"/>.</summary>
public enum ModuleSignatureStatus
{
    /// <summary>The signature verifies over the canonical form with a current, unrevoked publisher key.</summary>
    Valid,

    /// <summary>A required field is empty or a permission is blank.</summary>
    Malformed,

    /// <summary>The package carries no signature bytes.</summary>
    MissingSignature,

    /// <summary>The signer key id is not in the trust store.</summary>
    UnknownKey,

    /// <summary>The key is revoked, or the check time is outside its validity dates.</summary>
    KeyNotValid,

    /// <summary>The signature does not verify over the canonical form with this key.</summary>
    BadSignature,
}
