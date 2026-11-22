using System.Security.Cryptography;
using Bazaar.Contracts.Modules;

namespace Bazaar.Install.Trust;

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

        if (package.Signature is null || package.Signature.Length == 0)
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

        // The key proves who signed. These two checks decide whether that publisher may sign this package at all.
        if (!string.Equals(key.PublisherId, package.PublisherId, StringComparison.Ordinal))
        {
            return ModuleSignatureStatus.PublisherMismatch;
        }

        if (!key.Covers(package.ModuleId))
        {
            return ModuleSignatureStatus.ModuleNotInKeyScope;
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
        if (!ModuleText.IsToken(package.ModuleId)
            || !ModuleText.IsToken(package.Version)
            || !ModuleText.IsToken(package.PublisherId)
            || !ModuleText.IsToken(package.SignerKeyId)
            || !ModuleText.IsSha256Hex(package.ContentDigestSha256Hex)
            || package.DeclaredPermissions.Count > ModuleText.MaxPermissions)
        {
            return false;
        }

        if (package.Host is null || HostCompatibility.ValidateRequirements(package.Host).Count > 0)
        {
            return false;
        }

        return package.DeclaredPermissions is not null && package.DeclaredPermissions.All(ModuleText.IsToken);
    }
}
