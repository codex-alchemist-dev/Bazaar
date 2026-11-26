namespace Bazaar.Server.DataVolume;

/// <summary>Whether an instance may hold real data. Fixed at setup; a development instance can never become production (doc 63 section 8).</summary>
public enum InstanceClass
{
    Production,
    Development,
}

/// <summary>
/// The volume descriptor kept in the boot root (doc 63 section 2). It holds public facts and wrapped keys only, never a
/// plaintext key. The wraps bind the install id, volume id and KEK id, so an edited descriptor fails to unwrap.
/// </summary>
public sealed record DataVolumeDescriptor(
    int FormatVersion,
    string InstallId,
    string VolumeId,
    string ProviderKind,
    string ContainerKind,
    string ContainerPath,
    string Cipher,
    InstanceClass InstanceClass,
    string KekId,
    byte[] WrappedVolumeKey,
    byte[] WrappedMarkerKey,
    DateTimeOffset CreatedAt);

/// <summary>What the operating system reports about the volume mounted at the data root.</summary>
/// <param name="Mounted">The data root is the root of a mounted volume (not a plain folder on another volume).</param>
/// <param name="VolumeId">The OS volume id (BitLocker volume GUID or LUKS UUID); null when the provider has none.</param>
/// <param name="Encrypted">The volume is fully encrypted (conversion complete).</param>
/// <param name="ProtectionOn">Protection is on: the volume key is not exposed in clear on the disk.</param>
/// <param name="Cipher">Normalised cipher name, one of <see cref="DataVolumeCiphers"/>; null when not encrypted.</param>
public sealed record DataVolumeStatus(bool Mounted, string? VolumeId, bool Encrypted, bool ProtectionOn, string? Cipher);

/// <summary>Normalised cipher names. The floor is AES-256 in XTS mode (doc 63 section 13); weaker names are refused.</summary>
public static class DataVolumeCiphers
{
    /// <summary>BitLocker XTS-AES-256 and LUKS2 <c>aes-xts-plain64</c> with a 512-bit key.</summary>
    public const string XtsAes256 = "xts-aes-256";

    /// <summary>The <c>dev-plain</c> provider: no encryption.</summary>
    public const string None = "none";

    public static bool MeetsFloor(string? cipher) => string.Equals(cipher, XtsAes256, StringComparison.Ordinal);
}

/// <summary>What is known about the database files at rest (doc 63 section 5).</summary>
public enum DatabaseAtRest
{
    /// <summary>A managed database inside the verified volume.</summary>
    Verified,

    /// <summary>An external database whose encryption the owner attested. Bazaar cannot check it.</summary>
    AttestedNotVerified,
}

/// <summary>The result of a successful verification.</summary>
public sealed record DataVolumeVerification(DataVolumeDescriptor Descriptor, DataVolumeStatus Status, string? PostgresSystemIdentifier)
{
    /// <summary>What is known about the database at rest. <see cref="DatabaseAtRest.Verified"/> unless the database is external.</summary>
    public DatabaseAtRest DatabaseAtRest { get; init; } = DatabaseAtRest.Verified;

    /// <summary>The stable code shown by the status page and Kodo: <c>verified</c> or <c>attested_not_verified</c>.</summary>
    public string DatabaseAtRestCode => DatabaseAtRest == DatabaseAtRest.AttestedNotVerified ? "attested_not_verified" : "verified";
}
