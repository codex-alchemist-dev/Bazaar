namespace Bazaar.Server.Settings;

/// <summary>Outcome of <see cref="ISettingStore"/> writes. Rejections and holds carry the reason; nothing is thrown for them.</summary>
/// <param name="Key">The setting name as the caller sent it.</param>
/// <param name="Outcome">Accepted, PendingApproval or Rejected.</param>
/// <param name="ApprovalTier">The approval the change needs; None unless PendingApproval.</param>
/// <param name="Reason">Why it was rejected or held; null when accepted.</param>
public sealed record SettingWriteResult(string Key, SettingWriteOutcome Outcome, ApprovalTier ApprovalTier, string? Reason);
