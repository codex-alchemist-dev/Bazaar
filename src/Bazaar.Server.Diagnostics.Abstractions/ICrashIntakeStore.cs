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
