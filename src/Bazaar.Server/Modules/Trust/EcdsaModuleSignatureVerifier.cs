using System.Security.Cryptography;

namespace Bazaar.Server.Modules.Trust;

/// <summary>
/// Verifies module package signatures with ECDSA P-256 and SHA-256 (doc 48 section 1). Signatures are the fixed 64-byte
/// IEEE P1363 form. Built on the platform crypto only; no custom primitives.
/// </summary>
public sealed class EcdsaModuleSignatureVerifier : IModuleSignatureVerifier
{
    private readonly ITrustedKeyStore _keys;
    private readonly TimeProvider _timeProvider;

    public EcdsaModuleSignatureVerifier(ITrustedKeyStore keys, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ModuleSignatureStatus Verify(ModulePackage package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!IsWellFormed(package))
        {
            return ModuleSignatureStatus.Malformed;
        }

        if (package.Signature.Length == 0)
        {
            return ModuleSignatureStatus.MissingSignature;
        }

        var key = _keys.Find(package.SignerKeyId);
        if (key is null)
        {
            return ModuleSignatureStatus.UnknownKey;
        }

        var now = _timeProvider.GetUtcNow();
        if (key.Revoked || now < key.NotBefore || now >= key.NotAfter)
        {
            return ModuleSignatureStatus.KeyNotValid;
        }

        try
        {
            var message = ModuleCanonicalForm.Build(package);
            var valid = key.PublicKey.VerifyData(
                message,
                package.Signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return valid ? ModuleSignatureStatus.Valid : ModuleSignatureStatus.BadSignature;
        }
        catch (CryptographicException)
        {
            // A malformed signature is a bad signature, not a server fault.
            return ModuleSignatureStatus.BadSignature;
        }
    }

    private static bool IsWellFormed(ModulePackage package)
    {
        if (string.IsNullOrWhiteSpace(package.ModuleId)
            || string.IsNullOrWhiteSpace(package.Version)
            || string.IsNullOrWhiteSpace(package.ContentDigestSha256Hex)
            || string.IsNullOrWhiteSpace(package.SignerKeyId))
        {
            return false;
        }

        return package.DeclaredPermissions.All(permission => !string.IsNullOrWhiteSpace(permission));
    }
}
