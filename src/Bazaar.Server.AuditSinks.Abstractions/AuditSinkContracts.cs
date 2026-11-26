using System.Globalization;

namespace Bazaar.Server.AuditSinks;

/// <summary>
/// One committed audit row as a sink receives it (doc 48 section 5.4). <see cref="Payload"/> is the stored text, so
/// personal fields stay ciphertext (section 5.5): a sink never sees plaintext and has no way to ask for it.
/// </summary>
public sealed record AuditSinkRow(
    long Seq,
    DateTimeOffset Ts,
    string Actor,
    string Kind,
    string Payload,
    byte[] PrevHash,
    byte[] Hash)
{
    /// <summary>Idempotency key: the sequence number plus the row hash, so a re-delivery of the same row has the same key and a forked row does not.</summary>
    public string IdempotencyKey => "row:" + Seq.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(Hash);
}

/// <summary>A signed checkpoint as stored (doc 48 section 5.2). Sinks forward it unchanged and never re-sign it.</summary>
public sealed record AuditSinkCheckpoint(
    long FirstSeq,
    long LastSeq,
    long RowCount,
    byte[] ChainHeadHash,
    byte[] MerkleRoot,
    DateTimeOffset CreatedAt,
    string KeyId,
    byte[] Signature)
{
    /// <summary>Idempotency key: the range plus the head hash.</summary>
    public string IdempotencyKey => "cp:" + FirstSeq.ToString(CultureInfo.InvariantCulture) + "-"
        + LastSeq.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexStringLower(ChainHeadHash);
}

/// <summary>
/// A secondary audit sink. The primary chain is the append-only Postgres table; a sink only receives what is already
/// committed there. Delivery is at-least-once, so every method must be idempotent per <c>IdempotencyKey</c>. A sink
/// throws on failure; the dispatcher isolates it.
/// </summary>
public interface IAuditSink
{
    /// <summary>Stable name, used as the cursor key and in failure audit rows. Operator-visible; never a secret.</summary>
    string Name { get; }

    /// <summary>Delivers rows in ascending <c>Seq</c> order. Re-delivery of a row already delivered must not duplicate it.</summary>
    Task DeliverRowsAsync(IReadOnlyList<AuditSinkRow> rows, CancellationToken ct);

    /// <summary>Delivers checkpoints in ascending <c>LastSeq</c> order, under the same idempotency rule.</summary>
    Task DeliverCheckpointsAsync(IReadOnlyList<AuditSinkCheckpoint> checkpoints, CancellationToken ct);
}

/// <summary>How far a sink has been delivered. Zero means nothing yet.</summary>
public sealed record AuditSinkCursor(long LastRowSeq, long LastCheckpointSeq)
{
    public static AuditSinkCursor Start { get; } = new(0, 0);
}

/// <summary>Durable per-sink delivery cursor. The host implements it over a table; a sink that is added later starts at <see cref="AuditSinkCursor.Start"/>.</summary>
public interface IAuditSinkCursorStore
{
    Task<AuditSinkCursor> GetAsync(string sinkName, CancellationToken ct);

    /// <summary>Saves the cursor. Implementations must never move it backwards.</summary>
    Task SaveAsync(string sinkName, AuditSinkCursor cursor, CancellationToken ct);
}

/// <summary>Read side of the committed chain for the dispatcher. Read-only; the dispatcher never writes the chain.</summary>
public interface IAuditSinkSource
{
    /// <summary>Up to <paramref name="limit"/> committed rows with <c>Seq</c> above <paramref name="afterSeq"/>, ascending.</summary>
    Task<IReadOnlyList<AuditSinkRow>> ReadRowsAfterAsync(long afterSeq, int limit, CancellationToken ct);

    /// <summary>Up to <paramref name="limit"/> checkpoints with <c>LastSeq</c> above <paramref name="afterLastSeq"/>, ascending.</summary>
    Task<IReadOnlyList<AuditSinkCheckpoint>> ReadCheckpointsAfterAsync(long afterLastSeq, int limit, CancellationToken ct);
}

/// <summary>
/// Signs the daily export. Same shape as the server's checkpoint signer (Identity Service holds the key, doc 48
/// section 5.2); the host adapts one to the other, so this module never references the server project.
/// </summary>
public interface IAuditSinkSigner
{
    string KeyId { get; }

    byte[] Sign(ReadOnlyMemory<byte> data);
}

/// <summary>An encryption key and its identifier, from the recovery-bundle key class (doc 33).</summary>
public sealed record AuditSinkKey(string KeyId, byte[] Key);

/// <summary>Supplies the replica encryption key. The owner holds the matching key in the recovery bundle; the server never needs it back.</summary>
public interface IAuditSinkKeyProvider
{
    Task<AuditSinkKey> GetReplicaKeyAsync(CancellationToken ct);
}

/// <summary>
/// Write-only offsite target: there is deliberately no read, list or delete member (doc 48 section 5.4). Writing the
/// same <paramref name="objectName"/> again with the same bytes must be harmless.
/// </summary>
public interface IAuditReplicaWriter
{
    Task WriteAsync(string objectName, ReadOnlyMemory<byte> data, CancellationToken ct);
}

/// <summary>Sends one syslog message as a single datagram or frame. UDP and TCP implementations are the host's; tests inject a fake.</summary>
public interface ISyslogTransport
{
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
}

/// <summary>Writes one JSON line to an OpenTelemetry-style stream (a file, a pipe, a collector's socket).</summary>
public interface IAuditStreamWriter
{
    Task WriteLineAsync(string line, CancellationToken ct);
}
