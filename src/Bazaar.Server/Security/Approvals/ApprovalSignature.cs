using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// The message the owner's device signs for a decision, and its verification (doc 15, "Approval flow" step 4).
/// ECDSA P-256 with SHA-256, signature in the fixed 64-byte IEEE P1363 form, so any platform's key store can produce it.
/// </summary>
/// <remarks>
/// The signed text is versioned and line-separated. The device id is restricted to a fixed pattern, so no field can
/// contain a line break that would let two different decisions share one message.
/// </remarks>
public static partial class ApprovalSignature
{
    /// <summary>Prefix that versions the message format. Changing the layout means a new prefix.</summary>
    public const string Domain = "bazaar.approval.v1";

    /// <summary>Length of an IEEE P1363 P-256 signature.</summary>
    public const int SignatureLength = 64;

    /// <summary>Allowed device id: letters, digits, dot, underscore and hyphen, 1 to 128 characters.</summary>
    public const string DeviceIdPattern = "^[A-Za-z0-9._-]{1,128}$";

    [GeneratedRegex(DeviceIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();

    /// <summary>True when the device id has the allowed shape.</summary>
    public static bool IsValidDeviceId(string? deviceId) => deviceId is not null && DeviceIdRegex().IsMatch(deviceId);

    /// <summary>The exact bytes the device signs for this decision.</summary>
    /// <exception cref="ArgumentException">The device id has a disallowed shape.</exception>
    public static byte[] Message(Guid approvalId, ApprovalDecision decision, byte[] argsHash, DateTimeOffset expiresAt, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(argsHash);
        ArgumentNullException.ThrowIfNull(deviceId);
        if (!IsValidDeviceId(deviceId))
        {
            throw new ArgumentException("Device id has a disallowed shape.", nameof(deviceId));
        }

        var text = string.Join(
            '\n',
            Domain,
            approvalId.ToString("D"),
            decision == ApprovalDecision.Approve ? "approve" : "deny",
            Convert.ToHexStringLower(argsHash),
            expiresAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            deviceId);
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Verifies a signature against a device's public key. Returns false, never throws, for any malformed key or signature.
    /// </summary>
    public static bool Verify(byte[] publicKeySpki, byte[] message, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(publicKeySpki);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(signature);

        if (signature.Length != SignatureLength)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKeySpki, out _);
            if (key.KeySize != 256)
            {
                return false;
            }

            return key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
