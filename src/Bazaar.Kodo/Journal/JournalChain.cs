using System.Security.Cryptography;

namespace Bazaar.Kodo.Journal;

/// <summary>Result of <see cref="JournalChain.Verify"/>. <see cref="FirstBrokenIndex"/> is null when the chain is intact.</summary>
public sealed record JournalVerification(int? FirstBrokenIndex)
{
    /// <summary>True when every record links to its predecessor and its hash matches its bytes.</summary>
    public bool IsValid => FirstBrokenIndex is null;
}

/// <summary>
/// SHA-256 hash chain over journal records (doc 46 section 6.5). Record hash =
/// SHA-256(previous record hash || canonical bytes of the record). The first record links to 32 zero bytes.
/// </summary>
public static class JournalChain
{
    /// <summary>Length of every hash in the chain, in bytes.</summary>
    public const int HashLength = 32;

    /// <summary>The previous hash of the first record: 32 zero bytes. A new array on every call.</summary>
    public static byte[] GenesisHash() => new byte[HashLength];

    /// <summary>Computes SHA-256 over <paramref name="previousHash"/> followed by <paramref name="canonical"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="previousHash"/> is not 32 bytes.</exception>
    public static byte[] ComputeHash(ReadOnlySpan<byte> previousHash, ReadOnlySpan<byte> canonical)
    {
        if (previousHash.Length != HashLength)
        {
            throw new ArgumentException($"Previous hash must be {HashLength} bytes.", nameof(previousHash));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(previousHash);
        hash.AppendData(canonical);
        return hash.GetHashAndReset();
    }

    /// <summary>Builds the next record in a chain, with its hash computed. The timestamp is normalised to UTC.</summary>
    public static JournalRecord Create(long sequence, DateTimeOffset at, string kind, byte[] payload, byte[] previousHash)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(previousHash);

        JournalRecord draft = new()
        {
            Sequence = sequence,
            At = at.ToUniversalTime(),
            Kind = kind,
            Payload = [.. payload],
            PreviousHash = [.. previousHash],
            Hash = [],
        };

        byte[] hash = ComputeHash(draft.PreviousHash, JournalCodec.Encode(draft));
        return draft with { Hash = hash };
    }

    /// <summary>Walks the chain from the genesis hash and reports the first record that does not verify.</summary>
    public static JournalVerification Verify(IReadOnlyList<JournalRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        byte[] expectedPrevious = GenesisHash();
        for (int index = 0; index < records.Count; index++)
        {
            JournalRecord record = records[index];
            if (!IsLinked(record, index, expectedPrevious))
            {
                return new JournalVerification(index);
            }

            expectedPrevious = record.Hash;
        }

        return new JournalVerification(null);
    }

    /// <summary>Stable key for a record in a set: the hex form of its hash.</summary>
    public static string KeyOf(JournalRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Convert.ToHexString(record.Hash);
    }

    private static bool IsLinked(JournalRecord record, int index, byte[] expectedPrevious)
    {
        if (record.Sequence != index || !record.PreviousHash.AsSpan().SequenceEqual(expectedPrevious))
        {
            return false;
        }

        try
        {
            byte[] expected = ComputeHash(record.PreviousHash, JournalCodec.Encode(record));
            return record.Hash.AsSpan().SequenceEqual(expected);
        }
        catch (ArgumentException)
        {
            // A malformed field (bad kind length, short hash) means the record does not verify.
            return false;
        }
    }
}
