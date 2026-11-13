using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>A backup set listed in the bundle, with the checksum of its files (hex SHA-256).</summary>
public sealed record RecoveryBackupSetEntry(string Name, string Sha256Hex, long SizeBytes);

/// <summary>An installed module listed in the bundle, with its version and signature (hex).</summary>
public sealed record RecoveryModuleEntry(string Id, string Version, string SignatureHex);

/// <summary>
/// The plaintext content of a recovery bundle (doc 48 section 6). Holds bootstrap references only, never secrets
/// beyond the sealed KEK export and the owner recovery material, which are already sealed by their own keys.
/// </summary>
/// <param name="Version">Payload format version. Must equal <see cref="CurrentVersion"/>.</param>
/// <param name="BootstrapReferences">Configuration references needed to find the rest of the install (doc 03).</param>
/// <param name="KekExport">Sealed key-encryption-key export. Empty when no export is included.</param>
/// <param name="AuditCheckpointHead">Hash of the audit chain head at the time of sealing, or null.</param>
/// <param name="BackupSets">Backup sets with their checksums.</param>
/// <param name="Modules">Installed modules with versions and signatures.</param>
/// <param name="OwnerRecoveryMaterial">Owner recovery material. Empty when none is included.</param>
/// <remarks>
/// <see cref="KekExport"/> and <see cref="OwnerRecoveryMaterial"/> are secret material. Dispose the payload when done
/// with it to zero both arrays. <see cref="RecoveryBundleSealer.Unseal"/> returns a payload that the caller owns and
/// must dispose.
/// </remarks>
public sealed record RecoveryBundlePayload(
    int Version,
    IReadOnlyDictionary<string, string> BootstrapReferences,
    byte[] KekExport,
    byte[]? AuditCheckpointHead,
    IReadOnlyList<RecoveryBackupSetEntry> BackupSets,
    IReadOnlyList<RecoveryModuleEntry> Modules,
    byte[] OwnerRecoveryMaterial) : IDisposable
{
    /// <summary>The only payload version this build reads and writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Zeroes the secret arrays. Safe to call more than once and on null or empty arrays. The arrays are overwritten in
    /// place, so copies made by the caller are not affected.
    /// </summary>
    public void Dispose()
    {
        ZeroIfNotEmpty(KekExport);
        ZeroIfNotEmpty(OwnerRecoveryMaterial);
    }

    private static void ZeroIfNotEmpty(byte[]? secret)
    {
        if (secret is { Length: > 0 })
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }
}

/// <summary>Source-generated JSON for <see cref="RecoveryBundlePayload"/> (no reflection serializer).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecoveryBundlePayload))]
internal sealed partial class RecoveryBundleJsonContext : JsonSerializerContext;
