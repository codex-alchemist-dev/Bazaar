using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Client.Approvals;

/// <summary>
/// The canonical bytes the device key signs for one decision: approval id, decision, content hash, expiry and key id. The
/// layout is the server's <c>bazaar.approval.v1</c> message (doc 15, approval flow step 4), so the server verifies it
/// unchanged. Fields are restricted so none can add a line that would let two decisions share one message.
/// </summary>
public static partial class ApprovalSigningPayload
{
    /// <summary>Prefix that versions the layout. It matches the server's decision domain.</summary>
    public const string Domain = "bazaar.approval.v1";

    /// <summary>Length of an IEEE P1363 P-256 signature.</summary>
    public const int SignatureLength = 64;

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();

    [GeneratedRegex(@"^(?:[0-9a-f]{2}){16,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ContentHashRegex();

    /// <summary>True when the device id has the allowed shape.</summary>
    public static bool IsValidDeviceId(string? deviceId) => deviceId is not null && DeviceIdRegex().IsMatch(deviceId);

    /// <summary>True when the hash is lowercase hex of 16 to 64 bytes.</summary>
    public static bool IsValidContentHash(string? hash) => hash is not null && ContentHashRegex().IsMatch(hash);

    /// <summary>Builds the bytes to sign. Throws on a field with a disallowed shape, so nothing malformed is ever signed.</summary>
    /// <exception cref="ArgumentException">The hash or device id has a disallowed shape.</exception>
    public static byte[] Build(Guid approvalId, bool approve, string contentHash, DateTimeOffset expiresAt, string deviceId)
    {
        if (!IsValidContentHash(contentHash))
        {
            throw new ArgumentException("Content hash must be lowercase hex.", nameof(contentHash));
        }

        if (!IsValidDeviceId(deviceId))
        {
            throw new ArgumentException("Device id has a disallowed shape.", nameof(deviceId));
        }

        var text = string.Join(
            '\n',
            Domain,
            approvalId.ToString("D"),
            approve ? "approve" : "deny",
            contentHash,
            expiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            deviceId);
        return Encoding.UTF8.GetBytes(text);
    }
}
