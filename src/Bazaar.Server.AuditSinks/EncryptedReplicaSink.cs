using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.AuditSinks;

/// <summary>
/// Encrypted, write-only offsite replica (doc 48 section 5.4). Each batch becomes one object: NDJSON of rows or
/// checkpoints sealed with AES-256-GCM under the recovery-bundle-class key (doc 33), the object name bound in as
/// associated data so an object cannot be passed off under another name. The only dependency that touches the offsite
/// copy is <see cref="IAuditReplicaWriter"/>, which has no read, list or delete member: this sink cannot read back,
/// and the owner's verifier is the only reader.
/// </summary>
/// <remarks>
/// Object layout: <c>"BZAS1"</c>, key id length (2 bytes, big endian), key id (UTF-8), nonce (12), tag (16), ciphertext.
/// A name is derived from the batch's range and last hash, so the same batch re-delivered overwrites itself; a batch cut
/// differently adds a second object whose lines carry the same idempotency keys, which the verifier drops.
/// </remarks>
public sealed class EncryptedReplicaSink : IAuditSink
{
    internal const string Magic = "BZAS1";
    private const int _keySize = 32;
    private readonly IAuditSinkKeyProvider _keys;
    private readonly IAuditReplicaWriter _writer;

    public EncryptedReplicaSink(IAuditSinkKeyProvider keys, IAuditReplicaWriter writer)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(writer);
        _keys = keys;
        _writer = writer;
    }

    public string Name => "offsite-replica";

    public Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return Task.CompletedTask;
        }

        var name = "audit/rows/" + Num(rows[0].Seq) + "-" + Num(rows[^1].Seq) + "-" + Convert.ToHexStringLower(rows[^1].Hash)[..16] + ".bzas";
        return SealAndWriteAsync(name, rows.Select(AuditSinkJson.Row), ct);
    }

    public Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (checkpoints.Count == 0)
        {
            return Task.CompletedTask;
        }

        var name = "audit/checkpoints/" + Num(checkpoints[0].LastSeq) + "-" + Num(checkpoints[^1].LastSeq) + "-" + Convert.ToHexStringLower(checkpoints[^1].ChainHeadHash)[..16] + ".bzas";
        return SealAndWriteAsync(name, checkpoints.Select(AuditSinkJson.Checkpoint), ct);
    }

    /// <summary>Opens an object. Used by the owner's verifier and by tests; the sink itself never calls it.</summary>
    internal static string Open(byte[] blob, string objectName, byte[] key)
    {
        var span = blob.AsSpan();
        if (!span.StartsWith(Encoding.ASCII.GetBytes(Magic)))
        {
            throw new FormatException("Not an audit replica object.");
        }

        var offset = Magic.Length;
        var idLength = (span[offset] << 8) | span[offset + 1];
        offset += 2 + idLength;
        var nonce = span.Slice(offset, 12);
        var tag = span.Slice(offset + 12, 16);
        var cipher = span[(offset + 28)..];
        var plain = new byte[cipher.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(objectName));
        return Encoding.UTF8.GetString(plain);
    }

    private static string Num(long n) => n.ToString("D20", CultureInfo.InvariantCulture);

    private async Task SealAndWriteAsync(string name, IEnumerable<string> lines, CancellationToken ct)
    {
        var key = await _keys.GetReplicaKeyAsync(ct).ConfigureAwait(false);
        if (key.Key.Length != _keySize)
        {
            throw new InvalidOperationException("The replica key must be 256 bits.");
        }

        var plain = new UTF8Encoding(false).GetBytes(string.Concat(lines.Select(l => l + "\n")));
        var keyId = Encoding.UTF8.GetBytes(key.KeyId);
        if (keyId.Length > ushort.MaxValue)
        {
            throw new InvalidOperationException("The replica key id is too long.");
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key.Key, tag.Length))
        {
            gcm.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(name));
        }

        using var blob = new MemoryStream();
        blob.Write(Encoding.ASCII.GetBytes(Magic));
        blob.WriteByte((byte)(keyId.Length >> 8));
        blob.WriteByte((byte)(keyId.Length & 0xFF));
        blob.Write(keyId);
        blob.Write(nonce);
        blob.Write(tag);
        blob.Write(cipher);

        await _writer.WriteAsync(name, blob.ToArray(), ct).ConfigureAwait(false);
    }
}
