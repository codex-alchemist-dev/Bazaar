using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Security.Audit.Checkpoints;
using Bazaar.Server.Security.Audit.Db;

namespace Bazaar.Server.Tests.Security.Audit.Anchors;

/// <summary>Shared fixtures for the anchor tests: checkpoints, a DER writer independent of the production encoder, and time.</summary>
internal static class AnchorFixtures
{
    public static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A checkpoint over seq 1..5 with fixed hashes, created at <see cref="Now"/>.</summary>
    public static StoredAuditCheckpoint Stored(byte chainFill = 0x11, byte rootFill = 0x22, long lastSeq = 5)
    {
        var checkpoint = new AuditCheckpoint(
            FirstSeq: 1,
            LastSeq: lastSeq,
            RowCount: lastSeq,
            ChainHeadHash: Enumerable.Repeat(chainFill, 32).ToArray(),
            MerkleRoot: Enumerable.Repeat(rootFill, 32).ToArray(),
            CreatedAt: Now);
        return new StoredAuditCheckpoint(checkpoint, "audit-key-1", Enumerable.Repeat((byte)0x5A, 64).ToArray());
    }

    /// <summary>The digest the service computes for a checkpoint: SHA-256 over its signed bytes.</summary>
    public static byte[] DigestOf(AuditCheckpoint checkpoint) => SHA256.HashData(checkpoint.SignedBytes());

    public static byte[] Hex(string text) =>
        Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal));

    /// <summary>Concatenates byte arrays.</summary>
    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A DER element with definite length (short or long form). Written here, not reused from production code.</summary>
    public static byte[] Tlv(byte tag, byte[] content)
    {
        var header = content.Length < 0x80
            ? new[] { tag, (byte)content.Length }
            : content.Length <= 0xFF
                ? new[] { tag, (byte)0x81, (byte)content.Length }
                : new[] { tag, (byte)0x82, (byte)(content.Length >> 8), (byte)(content.Length & 0xFF) };
        return Cat(header, content);
    }

    public static byte[] Seq(params byte[][] parts) => Tlv(0x30, Cat(parts));

    public static byte[] Int(params byte[] value) => Tlv(0x02, value);

    public static byte[] Oid(params byte[] content) => Tlv(0x06, content);

    /// <summary>The SHA-256 AlgorithmIdentifier as written in the RFC 3161 request: SEQUENCE { OID 2.16.840.1.101.3.4.2.1, NULL }.</summary>
    public static byte[] Sha256Alg() => Seq(Oid(0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01), Hex("05 00"));

    public static byte[] MessageImprint(byte[] digest) => Seq(Sha256Alg(), Tlv(0x04, digest));

    /// <summary>A TSTInfo with the given digest, serial number 7 and an optional nonce.</summary>
    public static byte[] TstInfo(byte[] digest, byte[]? nonce)
    {
        var parts = new List<byte[]>
        {
            Int(0x01),
            Oid(0x2A, 0x03, 0x04),
            MessageImprint(digest),
            Int(0x07),
            Tlv(0x18, Encoding.ASCII.GetBytes("20261115120000Z")),
        };
        if (nonce is not null)
        {
            parts.Add(Int(nonce));
        }

        return Seq(parts.ToArray());
    }

    /// <summary>
    /// A TimeStampToken: ContentInfo { signedData OID, [0] SignedData { version, empty digestAlgorithms, encapContentInfo { TSTInfo OID, [0] OCTET STRING } } }.
    /// No signer or certificate is included; the target does not check them.
    /// </summary>
    public static byte[] Token(byte[] tstInfo)
    {
        var encap = Seq(
            Oid(0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x09, 0x10, 0x01, 0x04),
            Tlv(0xA0, Tlv(0x04, tstInfo)));
        var signedData = Seq(Int(0x03), Tlv(0x31, []), encap);
        return Seq(Oid(0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x07, 0x02), Tlv(0xA0, signedData));
    }

    /// <summary>A TimeStampResp: PKIStatusInfo { status } and, for a grant, the token.</summary>
    public static byte[] Response(int status, byte[]? token)
    {
        var statusInfo = Seq(Int((byte)status));
        return token is null ? Seq(statusInfo) : Seq(statusInfo, token);
    }
}
