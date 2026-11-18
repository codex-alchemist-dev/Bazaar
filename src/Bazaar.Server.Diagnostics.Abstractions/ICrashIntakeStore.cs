namespace Bazaar.Server.Diagnostics;

/// <summary>
/// Durable storage for reports, groups and the filing outbox (doc 43 section 12.1 step 7). The Postgres implementation commits
/// each <see cref="CrashCommit"/> in one transaction. Everything the store holds is either sealed or free of personal data.
/// </summary>
public interface ICrashIntakeStore
{
    /// <summary>Returns the group with this fingerprint, or null.</summary>
    Task<StoredCrashGroup?> GetGroupAsync(string fingerprint, CancellationToken ct = default);

    /// <summary>Counts <c>FileIssue</c> outbox rows decided on this UTC day. This is the number the daily budget is checked against.</summary>
    Task<int> CountIssueFilingsAsync(DateOnly utcDay, CancellationToken ct = default);

    /// <summary>True when an unfinished, not parked <c>FileIssue</c> row exists for the group, so a second filing must not be queued.</summary>
    Task<bool> HasPendingFileIssueAsync(string fingerprint, CancellationToken ct = default);

    /// <summary>Lists groups waiting in <c>FilingDeferred</c>, oldest first, for the release pass.</summary>
    Task<IReadOnlyList<StoredCrashGroup>> ListDeferredGroupsAsync(int max, CancellationToken ct = default);

    /// <summary>Applies one change atomically. See <see cref="CrashCommitStatus"/> for the outcomes.</summary>
    Task<CrashCommitStatus> CommitAsync(CrashCommit commit, CancellationToken ct = default);
}

/// <summary>The filing outbox. A row is leased, carried out, then completed, or failed with a retry time.</summary>
public interface ICrashActionOutbox
{
    /// <summary>Returns up to <paramref name="max"/> rows that are due at <paramref name="now"/>, oldest first.</summary>
    Task<IReadOnlyList<PendingCrashAction>> GetDueAsync(DateTimeOffset now, int max, CancellationToken ct = default);

    /// <summary>Removes a row that was carried out, or is no longer needed.</summary>
    Task CompleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records a failed attempt and the earliest time to try again.</summary>
    Task FailAsync(Guid id, DateTimeOffset notBefore, string reason, CancellationToken ct = default);

    /// <summary>Parks a row that failed too many times. It stays visible to the operator and is never deleted silently.</summary>
    Task DeadLetterAsync(Guid id, string reason, CancellationToken ct = default);
}

/// <summary>Seals and opens report and screenshot bodies with the person's data key, or the company key for a shared device (doc 43 section 11.3).</summary>
public interface ICrashPayloadSealer
{
    /// <summary>Seals <paramref name="plaintext"/> for the install's key. The kind and record id are bound as associated data.</summary>
    Task<SealedPayload> SealAsync(Guid installId, SealedPayloadKind kind, Guid recordId, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default);

    /// <summary>Opens and authenticates a payload. Throws <see cref="System.Security.Cryptography.CryptographicException"/> on tampering or a destroyed key.</summary>
    Task<byte[]> OpenAsync(Guid installId, SealedPayloadKind kind, Guid recordId, SealedPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Files and updates issues through the system GitHub App (doc 21, doc 14 broker). The module never sees a credential.
/// An implementation throws <see cref="CrashIssueNotFoundException"/> when the issue no longer exists.
/// </summary>
public interface ICrashIssueFiler
{
    /// <summary>Creates the issue and returns its number.</summary>
    Task<int> CreateIssueAsync(Bazaar.Diagnostics.Grouping.IssueText issue, CancellationToken ct = default);

    /// <summary>Adds a comment to the issue.</summary>
    Task CommentAsync(int issueNumber, string text, CancellationToken ct = default);

    /// <summary>Reopens the closed issue and adds the comment.</summary>
    Task ReopenAsync(int issueNumber, string text, CancellationToken ct = default);
}

/// <summary>The issue was deleted by hand (doc 43 section 8.4). The group records it and the next occurrence files a new one.</summary>
public sealed class CrashIssueNotFoundException : Exception
{
    /// <summary>Creates the exception for an issue number.</summary>
    public CrashIssueNotFoundException(int issueNumber)
        : base($"Issue {issueNumber} no longer exists.")
    {
        IssueNumber = issueNumber;
    }

    /// <summary>The missing issue.</summary>
    public int IssueNumber { get; }
}

/// <summary>How long crash data is kept (doc 43 section 11.4).</summary>
/// <param name="ReportRetention">Sealed report bodies. Default 90 days.</param>
/// <param name="AttachmentRetention">Screenshots. Default 30 days.</param>
/// <param name="GroupRetention">Group summaries, counted from the last occurrence. Default 365 days.</param>
public sealed record CrashRetentionPolicy(TimeSpan ReportRetention, TimeSpan AttachmentRetention, TimeSpan GroupRetention)
{
    /// <summary>The shipped defaults.</summary>
    public static CrashRetentionPolicy Default { get; } = new(TimeSpan.FromDays(90), TimeSpan.FromDays(30), TimeSpan.FromDays(365));
}

/// <summary>What one retention pass removed.</summary>
public sealed record CrashPurgeResult(int Attachments, int Reports, int Groups);

/// <summary>Deletes crash data past its retention. Safe to run at any time and to run twice.</summary>
public interface ICrashRetention
{
    /// <summary>Removes screenshots, reports and groups older than the policy allows at <paramref name="now"/>. A group with a report left, or with an unfinished outbox row, is kept.</summary>
    Task<CrashPurgeResult> PurgeAsync(DateTimeOffset now, CrashRetentionPolicy policy, CancellationToken ct = default);
}

/// <summary>Maps installs to people so sealing and erasure can find the right key and rows. The identity component supplies it.</summary>
public interface ICrashInstallDirectory
{
    /// <summary>The person who owns the install, or null for a shared device, which is sealed with the company key.</summary>
    Task<Guid?> GetOwnerAsync(Guid installId, CancellationToken ct = default);

    /// <summary>Every install the person has ever registered, including retired ones.</summary>
    Task<IReadOnlyList<Guid>> GetInstallsAsync(Guid personId, CancellationToken ct = default);
}

/// <summary>What erasing a set of installs removed.</summary>
public sealed record CrashEraseResult(int Reports, int Attachments);

/// <summary>Removes the stored reports and screenshots of given installs. Group summaries stay, because they hold no personal data (doc 43 section 11.3).</summary>
public interface ICrashErasure
{
    /// <summary>Deletes every report, with its screenshots, that the installs sent. Safe to run twice.</summary>
    Task<CrashEraseResult> EraseInstallsAsync(IReadOnlyCollection<Guid> installIds, CancellationToken ct = default);
}

/// <summary>Binds an install id to the authenticated device on first sight (doc 32 "Crash install binding", PROPOSED).</summary>
public interface ICrashInstallRegistry
{
    /// <summary>
    /// Binds <paramref name="installId"/> to <paramref name="deviceId"/> when it is new, and returns true. Returns true when it is
    /// already bound to the same device, and false when it is bound to another one. <paramref name="isShared"/> only applies to a new binding.
    /// </summary>
    Task<bool> TryBindAsync(Guid installId, Guid deviceId, bool isShared, CancellationToken ct = default);
}
