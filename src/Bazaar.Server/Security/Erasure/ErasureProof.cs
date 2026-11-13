using System.Buffers.Binary;
using System.Text;

namespace Bazaar.Server.Security.Erasure;

/// <summary>
/// The signed proof of a crypto-erase (design doc 48, section 9): the per-subject keys were destroyed and a tombstone was
/// appended. The proof names the subject by a salted hash, the key ids destroyed, the scopes covered, the backup expiry
/// after which the data is unrecoverable from backups, the time, and the signing key. The proof is stored in the audit
/// chain. The signing key is held by the Identity Service; this record only defines the bytes that get signed.
/// </summary>
/// <remarks>
/// Instances are created only through <see cref="Create"/>. The constructor is private and every property is read-only,
/// so no other path builds a proof, and a copy made with <c>with</c> cannot set a field either.
/// Byte arrays are compared by content in <see cref="Equals(ErasureProof?)"/>.
/// </remarks>
public sealed record ErasureProof
{
    /// <summary>Domain tag at the start of the canonical encoding. Changing the encoding means a new tag.</summary>
    public const string SignedDomain = "BZ-ERASE-PROOF-v1";

    private ErasureProof(
        byte[] subjectRef,
        string[] keyIds,
        string[] scopes,
        DateTimeOffset backupExpiry,
        DateTimeOffset erasedAt,
        string signingKeyId)
    {
        SubjectRef = subjectRef;
        KeyIds = keyIds;
        Scopes = scopes;
        BackupExpiry = backupExpiry;
        ErasedAt = erasedAt;
        SigningKeyId = signingKeyId;
    }

    /// <summary>Salted hash of the subject id (see <see cref="SubjectRefHasher"/>). Never the name.</summary>
    public byte[] SubjectRef { get; }

    /// <summary>Ids of the per-subject keys destroyed, sorted (ordinal) and distinct.</summary>
    public IReadOnlyList<string> KeyIds { get; }

    /// <summary>Scopes covered by the erasure, sorted (ordinal) and distinct.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>The date after which the erased data is unrecoverable from backups (UTC).</summary>
    public DateTimeOffset BackupExpiry { get; }

    /// <summary>When the keys were destroyed (UTC).</summary>
    public DateTimeOffset ErasedAt { get; }

    /// <summary>Identifier of the key that signs this proof.</summary>
    public string SigningKeyId { get; }

    /// <summary>
    /// Validates the inputs and builds a proof. Key ids and scopes are sorted on construction, so the order of the
    /// inputs does not matter. Throws <see cref="ArgumentException"/> when a value breaks a rule: no key ids, no scopes,
    /// an empty or duplicate id, an empty subject reference or signing key id, a timestamp that is not UTC, or a backup
    /// expiry earlier than the erasure time.
    /// </summary>
    public static ErasureProof Create(
        byte[] subjectRef,
        IEnumerable<string> keyIds,
        IEnumerable<string> scopes,
        DateTimeOffset backupExpiry,
        DateTimeOffset erasedAt,
        string signingKeyId)
    {
        ArgumentNullException.ThrowIfNull(subjectRef);
        ArgumentNullException.ThrowIfNull(keyIds);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(signingKeyId);

        if (subjectRef.Length == 0)
        {
            throw new ArgumentException("The subject reference must not be empty.", nameof(subjectRef));
        }

        if (string.IsNullOrWhiteSpace(signingKeyId))
        {
            throw new ArgumentException("The signing key id must not be empty.", nameof(signingKeyId));
        }

        if (erasedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("ErasedAt must be UTC.", nameof(erasedAt));
        }

        if (backupExpiry.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("BackupExpiry must be UTC.", nameof(backupExpiry));
        }

        if (backupExpiry < erasedAt)
        {
            throw new ArgumentException("BackupExpiry must not be earlier than ErasedAt.", nameof(backupExpiry));
        }

        return new ErasureProof(
            subjectRef.ToArray(),
            Normalize(keyIds, nameof(keyIds)),
            Normalize(scopes, nameof(scopes)),
            backupExpiry,
            erasedAt,
            signingKeyId);
    }

    /// <summary>
    /// Canonical bytes that the signer signs: a length-prefixed domain tag, then the fields in declaration order. Lists
    /// write a 32-bit count and then each entry as a length-prefixed field. Integers are big-endian 64-bit, the timestamps
    /// are UTC ticks, and byte arrays and strings carry a 32-bit length. Any change to any field changes these bytes.
    /// </summary>
    public byte[] SignedBytes()
    {
        using var stream = new MemoryStream();

        WriteField(stream, Encoding.UTF8.GetBytes(SignedDomain));
        WriteField(stream, SubjectRef);
        WriteList(stream, KeyIds);
        WriteList(stream, Scopes);
        WriteInt64(stream, BackupExpiry.UtcTicks);
        WriteInt64(stream, ErasedAt.UtcTicks);
        WriteField(stream, Encoding.UTF8.GetBytes(SigningKeyId));

        return stream.ToArray();
    }

    /// <summary>Value equality over the fields; byte arrays are compared by content.</summary>
    public bool Equals(ErasureProof? other) =>
        other is not null
        && SubjectRef.AsSpan().SequenceEqual(other.SubjectRef)
        && KeyIds.SequenceEqual(other.KeyIds, StringComparer.Ordinal)
        && Scopes.SequenceEqual(other.Scopes, StringComparer.Ordinal)
        && BackupExpiry.ToUniversalTime().UtcTicks == other.BackupExpiry.ToUniversalTime().UtcTicks
        && ErasedAt.ToUniversalTime().UtcTicks == other.ErasedAt.ToUniversalTime().UtcTicks
        && string.Equals(SigningKeyId, other.SigningKeyId, StringComparison.Ordinal);

    /// <summary>Hash code consistent with <see cref="Equals(ErasureProof?)"/>.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(SubjectRef);
        foreach (var keyId in KeyIds)
        {
            hash.Add(keyId, StringComparer.Ordinal);
        }

        foreach (var scope in Scopes)
        {
            hash.Add(scope, StringComparer.Ordinal);
        }

        hash.Add(BackupExpiry.ToUniversalTime().UtcTicks);
        hash.Add(ErasedAt.ToUniversalTime().UtcTicks);
        hash.Add(SigningKeyId, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static string[] Normalize(IEnumerable<string> values, string paramName)
    {
        var sorted = values.ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", paramName);
        }

        foreach (var value in sorted)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Values must not be empty.", paramName);
            }
        }

        Array.Sort(sorted, StringComparer.Ordinal);
        for (var i = 1; i < sorted.Length; i++)
        {
            if (string.Equals(sorted[i - 1], sorted[i], StringComparison.Ordinal))
            {
                throw new ArgumentException("Values must be distinct.", paramName);
            }
        }

        return sorted;
    }

    private static void WriteList(MemoryStream stream, IReadOnlyList<string> values)
    {
        WriteInt32(stream, values.Count);
        foreach (var value in values)
        {
            WriteField(stream, Encoding.UTF8.GetBytes(value));
        }
    }

    private static void WriteField(MemoryStream stream, byte[] value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(MemoryStream stream, int value)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, value);
        stream.Write(number);
    }

    private static void WriteInt64(MemoryStream stream, long value)
    {
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, value);
        stream.Write(number);
    }
}
