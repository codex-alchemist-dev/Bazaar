using Bazaar.Diagnostics.Grouping;
using Bazaar.Diagnostics.Ingestion;

namespace Bazaar.Server.Diagnostics;

/// <summary>
/// AEAD ciphertext of a report body or screenshot (doc 43 section 11.3). <see cref="KeyId"/> names the key that sealed it,
/// so an erasure proof can say which key was destroyed without naming content.
/// </summary>
public sealed record SealedPayload(string KeyId, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

/// <summary>What the sealed bytes are. It is bound into the associated data, so a blob cannot be moved to another record.</summary>
public enum SealedPayloadKind
{
    /// <summary>The serialised crash report.</summary>
    Report,

    /// <summary>The screenshot bytes.</summary>
    Attachment,
}

/// <summary>A report as stored: sealed body plus the plain, non-personal columns the server queries and expires by.</summary>
/// <param name="ReportId">The report's id, which makes a retried submission idempotent.</param>
/// <param name="InstallId">The install that sent it; the sealer picks the person or company key from it.</param>
/// <param name="Fingerprint">The server-computed fingerprint of the group it belongs to.</param>
/// <param name="CreatedAt">When the device built the report.</param>
/// <param name="ReceivedAt">When the server accepted it.</param>
/// <param name="ClientVersion">The client version as reported.</param>
/// <param name="Symbols">Whether the flagged frame was symbolicated.</param>
/// <param name="BodySha256">Lower-case hex SHA-256 of the plaintext JSON, for the audit entry. Never the content.</param>
/// <param name="Body">The sealed report JSON.</param>
public sealed record StoredCrashReport(
    Guid ReportId,
    Guid InstallId,
    string Fingerprint,
    DateTimeOffset CreatedAt,
    DateTimeOffset ReceivedAt,
    string ClientVersion,
    SymbolsState Symbols,
    string BodySha256,
    SealedPayload Body);

/// <summary>A kept screenshot as stored.</summary>
/// <param name="AttachmentId">The attachment id from the begin message.</param>
/// <param name="ReportId">The report that refers to it.</param>
/// <param name="ContentType">The declared and verified media type.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of the plaintext bytes, kept in the audit entry after expiry.</param>
/// <param name="Data">The sealed bytes.</param>
public sealed record StoredCrashAttachment(Guid AttachmentId, Guid ReportId, string ContentType, string Sha256, SealedPayload Data);

/// <summary>
/// The facts the issue needs that are not in <see cref="GroupState"/>. They hold no personal data (doc 43 section 8.5): the
/// code, type, top frames, flagged line and one short redacted message line.
/// </summary>
/// <param name="ErrorCode">The registered error code, or null.</param>
/// <param name="ErrorType">The exception type's full name.</param>
/// <param name="Frames">The stack, top frame first, as the fingerprint sees it.</param>
/// <param name="FlaggedLine">The flagged frame's line when symbols resolved, otherwise null.</param>
/// <param name="RedactedMessage">The device-redacted message.</param>
public sealed record CrashIssueFacts(
    string? ErrorCode,
    string ErrorType,
    IReadOnlyList<Bazaar.Diagnostics.Fingerprinting.FingerprintFrame> Frames,
    int? FlaggedLine,
    string? RedactedMessage);

/// <summary>A group as stored, with the optimistic-concurrency version a commit must present.</summary>
/// <param name="State">The grouping state.</param>
/// <param name="Facts">The facts the issue body needs.</param>
/// <param name="Version">Increases by one on every commit. A commit against a stale version is refused.</param>
public sealed record StoredCrashGroup(GroupState State, CrashIssueFacts Facts, long Version);

/// <summary>An effect waiting to be carried out against GitHub. It is written in the same commit as the state change that decided it.</summary>
/// <param name="Id">Unique id of the outbox row.</param>
/// <param name="Fingerprint">The group it belongs to.</param>
/// <param name="Action">What to do: <see cref="FileIssue"/>, <see cref="AddComment"/> or <see cref="Reopen"/>.</param>
/// <param name="CreatedAt">When it was decided.</param>
/// <param name="Attempts">Failed attempts so far.</param>
/// <param name="NotBefore">Earliest time to try again.</param>
public sealed record PendingCrashAction(Guid Id, string Fingerprint, GroupAction Action, DateTimeOffset CreatedAt, int Attempts, DateTimeOffset NotBefore);

/// <summary>
/// One atomic change: optionally a report and its screenshot, and the group's new state, with the outbox rows the change decided.
/// The store applies all of it or none of it.
/// </summary>
/// <param name="Report">The report to store, or null for a group-only change such as releasing a deferred filing.</param>
/// <param name="Attachment">The screenshot to store, or null.</param>
/// <param name="Group">The group's new state and facts.</param>
/// <param name="ExpectedGroupVersion">The version the change was computed from, or null when the group is new.</param>
/// <param name="Outbox">The actions to carry out afterwards.</param>
public sealed record CrashCommit(
    StoredCrashReport? Report,
    StoredCrashAttachment? Attachment,
    StoredCrashGroup Group,
    long? ExpectedGroupVersion,
    IReadOnlyList<PendingCrashAction> Outbox);

/// <summary>How a commit ended.</summary>
public enum CrashCommitStatus
{
    /// <summary>Everything was written.</summary>
    Committed,

    /// <summary>The group changed since it was read. Nothing was written; read again and recompute.</summary>
    VersionConflict,

    /// <summary>A report with this id is already stored. Nothing was written. The submission was a retry.</summary>
    DuplicateReport,
}
