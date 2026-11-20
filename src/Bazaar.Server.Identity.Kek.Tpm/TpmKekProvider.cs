using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// The <c>tpm</c> KEK provider (doc 14, DECIDED 2026-11-19): the 32-byte KEK lives on disk only in TPM-sealed form and wraps use the
/// same AES-256-GCM as the other providers. If the TPM will not unseal (another chip, a failed PCR policy, a damaged file) opening
/// fails and reports; it never falls back to the <c>file</c> provider. On a VM host the default is <c>file</c>, so a virtual TPM is
/// refused unless the operator opted in, and then <see cref="Custody"/> says so for the audit log.
/// </summary>
public sealed class TpmKekProvider : AesGcmKekProvider
{
    /// <summary>Stable id stored with each wrap. A TPM wrap does not open under another provider's id and the reverse.</summary>
    public const string Id = "tpm-kek-v1";

    private TpmKekProvider(byte[] key, TpmCustodyClass custody)
        : base(Id, key)
    {
        Custody = custody;
    }

    /// <summary>Hardware or virtual TPM. Setup warns and the audit log records it when this is <see cref="TpmCustodyClass.Virtual"/>.</summary>
    public TpmCustodyClass Custody { get; }

    /// <summary>Opens the sealed KEK at <see cref="TpmOptions.KeyFile"/>, creating and sealing a fresh one when the file is absent.</summary>
    /// <exception cref="InvalidOperationException">This host is a VM and virtual TPM custody was not opted into.</exception>
    /// <exception cref="InvalidDataException">The TPM will not unseal the file, or it does not hold a 32-byte key.</exception>
    public static TpmKekProvider Open(TpmOptions options, ITpmSealer sealer, IVirtualizationProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(probe);

        var custody = probe.Detect();
        if (custody == TpmCustodyClass.Virtual && !options.AllowVirtual)
            throw new InvalidOperationException(
                $"This host is a virtual machine, so its TPM is virtual (weaker custody). Use identity.kek_provider 'file', or set {TpmOptions.AllowVirtualKey}=true to accept it (doc 14).");
        if (options.PublicKeyPath is { } publicKey && !File.Exists(publicKey))
            throw new FileNotFoundException("The PCR policy public key is missing; refusing to seal without the signed policy.", publicKey);

        var path = options.KeyFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new ArgumentException("The KEK file path has no directory."));

        if (!File.Exists(path))
            Create(path, options, sealer);

        var stored = File.ReadAllBytes(path);
        byte[] key;
        try
        {
            key = sealer.Unseal(stored, options);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException(
                "The TPM would not unseal the KEK file. This host fails closed and does not fall back to another provider; restore from the escrowed KEK copy (doc 14).",
                ex);
        }
        finally
        {
            Array.Clear(stored);
        }

        if (key.Length != 32)
        {
            Array.Clear(key);
            throw new InvalidDataException("The sealed KEK does not hold a 32-byte key.");
        }

        return new TpmKekProvider(key, custody);
    }

    private static void Create(string path, TpmOptions options, ITpmSealer sealer)
    {
        var fresh = RandomNumberGenerator.GetBytes(32);
        byte[] sealedKey;
        try
        {
            sealedKey = sealer.Seal(fresh, options);
        }
        finally
        {
            Array.Clear(fresh);
        }

        try
        {
            // CreateNew fails if another start created the file first; that start's key is read by the caller.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(sealedKey);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
        }

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
