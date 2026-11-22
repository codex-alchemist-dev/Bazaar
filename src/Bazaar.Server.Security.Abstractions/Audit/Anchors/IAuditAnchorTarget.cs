namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// An external place that a checkpoint digest is anchored to (design doc 48, section 5.3). Targets are outbound only: the
/// server calls out, and nothing calls in. A target never sees checkpoint content, only the 32-byte SHA-256 digest of
/// <see cref="Checkpoints.AuditCheckpoint.SignedBytes"/>.
/// </summary>
public interface IAuditAnchorTarget
{
    /// <summary>
    /// Stable identifier. It is stored with each receipt and named in audit events. Never a host name or a credential.
    /// </summary>
    string TargetId { get; }

    /// <summary>Anchors a 32-byte digest. Throws when the target does not accept it.</summary>
    Task<AuditAnchorReceipt> AnchorAsync(ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct);

    /// <summary>Returns true when <paramref name="receipt"/> from this target covers <paramref name="checkpointDigest"/>.</summary>
    Task<bool> VerifyAsync(AuditAnchorReceipt receipt, ReadOnlyMemory<byte> checkpointDigest, CancellationToken ct);
}
