using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// The <c>tpm</c> KEK provider (doc 14 "Linux TPM-sealed KEK", DECIDED 2026-11-19): the 32-byte KEK is sealed to the TPM and the sealed
/// blob lives in the key file; the plaintext key exists only in memory. Wraps use the same AES-256-GCM as the other providers. It fails
/// closed: a TPM that cannot unseal throws <see cref="KekCustodyException"/> and the host does not start on another provider. A new key
/// is proven by an unseal round trip before its file is written, so a bad seal never orphans wrapped DEKs.
/// </summary>
public sealed partial class TpmKekProvider : AesGcmKekProvider
{
    /// <summary>Stable id stored with each wrap. A TPM wrap does not open under another provider's id.</summary>
    public const string Id = "tpm-kek-v1";

    /// <summary>The custody class when the TPM is a virtual one (doc 14: weaker custody, never hardware for the doc 48 floors).</summary>
    public const string VirtualCustody = "virtual";

    /// <summary>The custody class for a TPM that looks physical.</summary>
    public const string HardwareCustody = "hardware";

    /// <summary>The custody class when the probe could not tell. Treated as weaker, never as hardware.</summary>
    public const string UnverifiedCustody = "unverified";

    private static readonly HashSet<string> _virtualManufacturers = new(StringComparer.OrdinalIgnoreCase)
    {
        "IBM", "MSFT", "QEMU", "GOOG", "VMW", "VBOX",
    };

    private TpmKekProvider(byte[] key, string custodyClass)
        : base(Id, key)
    {
        CustodyClass = custodyClass;
    }

    /// <summary>Custody class for the audit log (<see cref="AuditLogKekSink.RecordCustodyAsync"/>) and the setup warning.</summary>
    public string CustodyClass { get; }

    /// <summary>Opens the sealed key file at <paramref name="keyPath"/>, creating and sealing a fresh key when it is absent.</summary>
    /// <exception cref="KekCustodyException">The TPM or tool is unavailable, or the unseal fails.</exception>
    /// <exception cref="InvalidDataException">The unsealed key is not 32 bytes.</exception>
    public static TpmKekProvider Open(string keyPath, ITpmKeySealer sealer, string custodyClass)
    {
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentException.ThrowIfNullOrWhiteSpace(custodyClass);
        var key = KekKeyFile.OpenOrCreate(
            keyPath,
            seal: plain =>
            {
                var blob = sealer.Seal(plain);
                var proof = sealer.Unseal(blob);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(proof, plain))
                        throw new KekCustodyException("The TPM unsealed a different key than was sealed.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(proof);
                }

                return blob;
            },
            open: sealer.Unseal);
        return new TpmKekProvider(key, custodyClass);
    }

    /// <summary>
    /// Classifies the TPM from its manufacturer id. Only a probe that succeeds and names a non-virtual manufacturer returns
    /// <see cref="HardwareCustody"/>; a failed probe returns <see cref="UnverifiedCustody"/>.
    /// </summary>
    public static string ProbeCustodyClass(ITpmToolRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        try
        {
            var result = runner.Run("tpm2_getcap", ["properties-fixed"], null, Directory.GetCurrentDirectory());
            if (result.ExitCode != 0)
                return UnverifiedCustody;
            var match = ManufacturerPattern().Match(System.Text.Encoding.UTF8.GetString(result.StandardOutput));
            if (!match.Success)
                return UnverifiedCustody;
            return _virtualManufacturers.Contains(match.Groups[1].Value.Trim()) ? VirtualCustody : HardwareCustody;
        }
        catch (KekCustodyException)
        {
            return UnverifiedCustody;
        }
    }

    [GeneratedRegex("TPM2_PT_MANUFACTURER:.*?value:\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ManufacturerPattern();
}
