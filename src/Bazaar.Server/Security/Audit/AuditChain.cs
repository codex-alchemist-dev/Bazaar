using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.Security.Audit;

/// <summary>
/// Pure hash-chain logic for the audit log: append, verify. No I/O and no storage; the Postgres writer
/// (doc 15, role without UPDATE or DELETE) calls these and persists the result.
/// </summary>
public static class AuditChain
{
    /// <summary><c>prev_hash</c> of the first record in a chain.</summary>
    public static readonly byte[] GenesisHash = new byte[SHA256.HashSizeInBytes];

    /// <summary>Builds the next record after <paramref name="head"/> (null for an empty log). Seq is head.Seq + 1, or 1.</summary>
    public static AuditRecord Append(AuditRecord? head, DateTimeOffset ts, string actor, string kind, string payload)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(payload);

        var seq = head is null ? 1 : head.Seq + 1;
        var prevHash = head is null ? GenesisHash : head.Hash;
        var hash = ComputeHash(seq, ts, actor, kind, payload, prevHash);
        return new AuditRecord(seq, ts, actor, kind, payload, prevHash, hash);
    }

    /// <summary>
    /// Checks a full chain from genesis. When <paramref name="expectedHead"/> is given (the last hash from a
    /// signed export or an external anchor), a chain cut short at the tail fails even though its links hold.
    /// </summary>
    public static AuditVerification Verify(IReadOnlyList<AuditRecord> records, byte[]? expectedHead = null)
    {
        ArgumentNullException.ThrowIfNull(records);

        byte[] prevHash = GenesisHash;
        long expectedSeq = 1;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            if (record.Seq != expectedSeq)
            {
                // Reorder, gap or missing head: the first record must be seq 1 and each next one +1.
                return AuditVerification.Fail(i, record.Seq, AuditFailure.SequenceBroken);
            }

            if (!record.PrevHash.AsSpan().SequenceEqual(prevHash))
            {
                return AuditVerification.Fail(i, record.Seq, AuditFailure.PrevHashMismatch);
            }

            var recomputed = ComputeHash(record.Seq, record.Ts, record.Actor, record.Kind, record.Payload, record.PrevHash);
            if (!record.Hash.AsSpan().SequenceEqual(recomputed))
            {
                return AuditVerification.Fail(i, record.Seq, AuditFailure.HashMismatch);
            }

            prevHash = record.Hash;
            expectedSeq++;
        }

        if (expectedHead is not null && !prevHash.AsSpan().SequenceEqual(expectedHead))
        {
            return AuditVerification.Fail(records.Count, expectedSeq - 1, AuditFailure.HeadMismatch);
        }

        return AuditVerification.Ok(records.Count);
    }

    /// <summary>SHA-256 over length-prefixed fields, so no two different field tuples share an encoding.</summary>
    public static byte[] ComputeHash(long seq, DateTimeOffset ts, string actor, string kind, string payload, byte[] prevHash)
    {
        using var stream = new MemoryStream();
        Span<byte> number = stackalloc byte[8];

        BinaryPrimitives.WriteInt64BigEndian(number, seq);
        stream.Write(number);
        BinaryPrimitives.WriteInt64BigEndian(number, ts.ToUniversalTime().UtcTicks);
        stream.Write(number);
        WriteField(stream, actor);
        WriteField(stream, kind);
        WriteField(stream, payload);
        WriteField(stream, prevHash);

        return SHA256.HashData(stream.ToArray());
    }

    private static void WriteField(MemoryStream stream, string value) =>
        WriteField(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

public enum AuditFailure
{
    SequenceBroken,
    PrevHashMismatch,
    HashMismatch,
    HeadMismatch,
}

/// <summary>Result of <see cref="AuditChain.Verify"/>. <see cref="FailedIndex"/> is the offending position in the input list.</summary>
public sealed record AuditVerification(bool IsValid, int Checked, int FailedIndex, long FailedSeq, AuditFailure? Failure)
{
    public static AuditVerification Ok(int checkedCount) => new(true, checkedCount, -1, 0, null);

    public static AuditVerification Fail(int index, long seq, AuditFailure failure) =>
        new(false, index, index, seq, failure);
}
