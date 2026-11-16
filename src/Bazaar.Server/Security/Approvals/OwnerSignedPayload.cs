using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Security.Approvals;

/// <summary>
/// One owner-gated action as the owner's device signs it (doc 32 section 5a). Every action class uses this one shape:
/// merges, module permission approvals and legal holds. <paramref name="HeadSha"/> is set for merges only, so a new head
/// makes the signature void (doc 21).
/// </summary>
public sealed record OwnerPayload(
    string Action,
    Guid ApprovalId,
    byte[] ArgsHash,
    string? HeadSha,
    Guid HumanId,
    string DeviceId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    byte[] Nonce);

/// <summary>
/// The exact bytes an owner device signs for an <see cref="OwnerPayload"/>. Versioned, line-separated UTF-8 text. Every
/// free-text field is restricted to a fixed pattern, so no field can add a line that would let two payloads share one
/// message. Changing the layout means a new <see cref="Domain"/>.
/// </summary>
public static partial class OwnerSignedPayload
{
    /// <summary>Prefix that versions this layout. It is separate from the doc 15 decision domain, so the two never substitute for each other.</summary>
    public const string Domain = "bazaar.owner.v1";

    /// <summary>Length of the single-use nonce in bytes (128 bits).</summary>
    public const int NonceLength = 16;

    /// <summary>Action names are dotted lower-case words, for example <c>pr.merge</c> or <c>legal_hold.release</c>.</summary>
    public const string ActionPattern = @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+\z";

    [GeneratedRegex(ActionPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ActionRegex();

    [GeneratedRegex(@"^[0-9a-f]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HeadShaRegex();

    /// <summary>The exact bytes the device signs. Throws when a field has a disallowed shape, so nothing unsigned-looking is ever built.</summary>
    /// <exception cref="ArgumentException">A field has a disallowed shape.</exception>
    public static byte[] Build(OwnerPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!ActionRegex().IsMatch(payload.Action))
        {
            throw new ArgumentException("Action name has a disallowed shape.", nameof(payload));
        }

        ArgumentNullException.ThrowIfNull(payload.ArgsHash);
        if (payload.ArgsHash.Length == 0)
        {
            throw new ArgumentException("Args hash is empty.", nameof(payload));
        }

        if (payload.HeadSha is not null && !HeadShaRegex().IsMatch(payload.HeadSha))
        {
            throw new ArgumentException("Head sha must be 40 lower-case hex characters.", nameof(payload));
        }

        if (!ApprovalSignature.IsValidDeviceId(payload.DeviceId))
        {
            throw new ArgumentException("Device id has a disallowed shape.", nameof(payload));
        }

        ArgumentNullException.ThrowIfNull(payload.Nonce);
        if (payload.Nonce.Length != NonceLength)
        {
            throw new ArgumentException("Nonce must be 16 bytes.", nameof(payload));
        }

        var text = string.Join(
            '\n',
            Domain,
            payload.Action,
            payload.ApprovalId.ToString("D"),
            Convert.ToHexStringLower(payload.ArgsHash),
            payload.HeadSha ?? "-",
            payload.HumanId.ToString("D"),
            payload.DeviceId,
            payload.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            payload.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(payload.Nonce));
        return Encoding.UTF8.GetBytes(text);
    }
}
