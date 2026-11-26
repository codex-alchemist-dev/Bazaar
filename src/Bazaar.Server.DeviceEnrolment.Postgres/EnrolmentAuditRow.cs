using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.DeviceEnrolment.Postgres;

/// <summary>
/// The <c>audit_log</c> row one committed enrolment writes (doc 32 section 5b: "the audit row"). The module must not reference
/// Bazaar.Server, so this repeats the hash chain of <c>AuditChain</c> and the append lock of <c>PostgresAuditLog</c>, as
/// <c>PgAuditWriter</c> in the VM component does. The chain algorithm and the lock key must stay equal to the server's, or the audit
/// verifier fails. The payload carries ids only, never key material.
/// </summary>
internal static class EnrolmentAuditRow
{
    /// <summary>Must equal <c>PostgresAuditLog</c>'s key, so every writer serialises on one chain.</summary>
    public const long AppendLockKey = 0x4241554449544C47;

    /// <summary>The kind <c>PrivacyAuditKinds.ApprovalKeyEnrolled</c> names.</summary>
    public const string Kind = "approval_key.enrolled";

    public static readonly byte[] GenesisHash = new byte[SHA256.HashSizeInBytes];

    /// <summary>Keys sorted ordinally, no whitespace: the canonical form the privacy audit recorder writes.</summary>
    public static string Payload(Guid enrolmentId, Guid deviceId) =>
        "{\"device_id\":\"" + deviceId.ToString("D") + "\",\"enrolment_id\":\"" + enrolmentId.ToString("D") + "\"}";

    /// <summary>Cuts to microseconds, the precision Postgres stores, so the hashed timestamp is the one read back.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }

    /// <summary>SHA-256 over big-endian seq and UTC ticks, then length-prefixed actor, kind, payload and prev_hash.</summary>
    public static byte[] ComputeHash(long seq, DateTimeOffset ts, string actor, string kind, string payload, byte[] prevHash)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, seq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, ts.UtcTicks);
        stream.Write(number);
        WriteField(stream, Encoding.UTF8.GetBytes(actor));
        WriteField(stream, Encoding.UTF8.GetBytes(kind));
        WriteField(stream, Encoding.UTF8.GetBytes(payload));
        WriteField(stream, prevHash);
        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}
