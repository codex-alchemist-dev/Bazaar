namespace Bazaar.Server.Hive.Abstractions;

/// <summary>What happened to a copy request.</summary>
public enum HiveCopyStatus
{
    /// <summary>Eligible and waiting for the review window or an owner approval.</summary>
    Held,

    /// <summary>Written to the Vault.</summary>
    Copied,

    /// <summary>Not eligible. See <see cref="HiveCopyOutcome.Refusal"/>.</summary>
    Refused,
}

/// <summary>Why a copy was refused.</summary>
public enum HiveRefusal
{
    /// <summary>The origin or target project is empty, so no grant can apply.</summary>
    InvalidInput,

    /// <summary>The origin and target project are the same; Hive only moves lessons between projects.</summary>
    SameProject,

    /// <summary>No grant covers the origin project, target project and kind (the default).</summary>
    NoGrant,

    /// <summary>The kind is not in <c>hive.allowed_kinds</c>.</summary>
    KindNotAllowed,

    /// <summary>The source note is T0 or T1.</summary>
    TierTooLow,

    /// <summary>The source is a preference, private-memory-only note, GC text, Vault text or reputation.</summary>
    ClassNotShareable,

    /// <summary>The source note carries subject tags (personal data).</summary>
    SubjectTagged,

    /// <summary>The source was written in a tainted session or from external or quarantined content.</summary>
    TaintedSource,

    /// <summary>The source note is deprecated.</summary>
    NotActive,

    /// <summary>The note's memory kind does not match the requested Hive kind.</summary>
    KindMismatch,

    /// <summary>The source text is not a valid note.</summary>
    Unparseable,

    /// <summary>The sanitizer found a project, customer, person or secret pattern.</summary>
    SanitizerRejected,

    /// <summary>The same lesson is already copied or waiting for this target.</summary>
    Duplicate,

    /// <summary>The copy path failed the area rules.</summary>
    PathInvalid,

    /// <summary>The Vault commit failed; the copy stays held.</summary>
    CommitFailed,

    /// <summary>The held copy no longer exists (rejected, already released or never held).</summary>
    NotFound,
}

/// <summary>Result of a copy request or a release step.</summary>
/// <param name="Status">Held, copied or refused.</param>
/// <param name="Refusal">The reason when refused, otherwise null.</param>
/// <param name="Path">Vault path of the copy when known.</param>
/// <param name="PendingId">Id of the held copy when held.</param>
/// <param name="ReleaseAt">When the copy is released automatically; null when only an owner approval releases it.</param>
/// <param name="Detail">Short machine-readable detail (sanitizer finding codes, class names). Never matched text.</param>
public sealed record HiveCopyOutcome(
    HiveCopyStatus Status,
    HiveRefusal? Refusal,
    string? Path,
    Guid? PendingId,
    DateTimeOffset? ReleaseAt,
    string? Detail)
{
    /// <summary>A refused outcome.</summary>
    public static HiveCopyOutcome Refused(HiveRefusal refusal, string? detail = null) =>
        new(HiveCopyStatus.Refused, refusal, null, null, null, detail);

    /// <summary>A held outcome.</summary>
    public static HiveCopyOutcome Held(Guid id, string path, DateTimeOffset? releaseAt) =>
        new(HiveCopyStatus.Held, null, path, id, releaseAt, null);

    /// <summary>A copied outcome.</summary>
    public static HiveCopyOutcome Copied(string path) =>
        new(HiveCopyStatus.Copied, null, path, null, null, null);
}
