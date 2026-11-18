namespace Bazaar.Server.Security.Audit.Anchors;

/// <summary>
/// Proof that an anchor target accepted a checkpoint digest (design doc 48, section 5.3). <see cref="ReceiptBytes"/> is what
/// the target returns for later verification: a DER time-stamp token for RFC 3161, or the commit id for an offsite Git remote.
/// </summary>
/// <remarks>
/// <see cref="AnchoredAt"/> is cut to whole microseconds, the precision the anchor table stores.
/// </remarks>
public sealed record AuditAnchorReceipt(string TargetId, byte[] ReceiptBytes, DateTimeOffset AnchoredAt);
