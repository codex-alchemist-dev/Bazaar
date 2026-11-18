namespace Bazaar.Server.Hive.Abstractions;

/// <summary>A project id as the Hive sees it: an opaque, non-empty string. Hive never resolves it to a project.</summary>
public readonly record struct ProjectId(string Value)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>The memory trust tiers of doc 08 section 4. T0 and T1 never cross a project boundary.</summary>
public enum HiveTrust
{
    T0 = 0,
    T1 = 1,
    T2 = 2,
    T3 = 3,
}

/// <summary>
/// What a candidate for copying is. Only <see cref="Lesson"/> may cross projects; every other class is an excluded class
/// (doc 08 section 6, doc 25 Hive row), and settings can never add one to the allowed set.
/// </summary>
public enum HiveSourceClass
{
    Lesson = 0,
    Preference,
    Reputation,
    PrivateMemory,
    GcText,
    VaultText,
    Credential,
}

/// <summary>The three kinds doc 08 allows to cross (<c>hive.allowed_kinds</c> is a subset of these).</summary>
public static class HiveKinds
{
    public const string Procedure = "procedure";
    public const string Failure = "failure";
    public const string TechnicalFact = "technical-fact";

    /// <summary>The floor: settings may narrow this set, never widen it.</summary>
    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Procedure, Failure, TechnicalFact };
}

/// <summary>
/// A lesson an agent in project <see cref="Project"/> proposes to copy (doc 25 <c>LessonRef</c>). Carries the text, so a copy never
/// has to read another project's Vault or GC text.
/// </summary>
/// <param name="Project">Origin project.</param>
/// <param name="MemoryId">Origin memory note id (written as <c>origin_memory</c>).</param>
/// <param name="Kind">One of <see cref="HiveKinds.All"/>.</param>
/// <param name="SourceClass">What the item is; anything but <see cref="HiveSourceClass.Lesson"/> is refused.</param>
/// <param name="Trust">Trust tier of the lesson.</param>
/// <param name="OriginKind">The doc 15 provenance label of the lesson (for example <c>agent</c>).</param>
/// <param name="SubjectTagged">True when the item is tagged with a person or other subject; refused.</param>
public sealed record LessonRef(
    ProjectId Project,
    string MemoryId,
    string Kind,
    HiveSourceClass SourceClass,
    HiveTrust Trust,
    string OriginKind,
    bool SubjectTagged,
    string Title,
    string Body);

/// <summary>One row of the owner's grant matrix: <see cref="FromProject"/> may copy lessons of <see cref="Kind"/> into <see cref="ToProject"/>.</summary>
public sealed record HiveGrant(ProjectId FromProject, ProjectId ToProject, string Kind, string GrantedBy, DateTimeOffset GrantedAt);

/// <summary>The Hive settings the flow reads (doc 08 <c>hive.allowed_kinds</c>, doc 25 <c>hive.grant_review_days</c>).</summary>
/// <param name="AllowedKinds">Kinds that may cross. Values outside <see cref="HiveKinds.All"/> are ignored.</param>
/// <param name="GrantReviewDays">Review window. 0 keeps every batch in review until the owner approves; negative counts as 0.</param>
public sealed record HiveOptions(IReadOnlySet<string> AllowedKinds, int GrantReviewDays)
{
    public const int DefaultGrantReviewDays = 30;

    public static HiveOptions Default { get; } = new(HiveKinds.All, DefaultGrantReviewDays);
}

public enum HiveCopyState
{
    InReview = 0,
    Active,
    Deprecated,
}

/// <summary>
/// A copy of a lesson. While <see cref="HiveCopyState.InReview"/> the sanitised text is held here and is not in the Vault;
/// on release it is written to <see cref="Path"/>. Trust and origin are labels, never instructions.
/// </summary>
public sealed record HiveCopy(
    Guid CopyId,
    ProjectId FromProject,
    ProjectId ToProject,
    string Kind,
    string OriginMemory,
    string OriginKind,
    HiveTrust Trust,
    string Title,
    string Body,
    string Path,
    HiveCopyState State,
    DateTimeOffset HeldAt,
    DateTimeOffset? ReviewUntil,
    DateTimeOffset? ReleasedAt,
    DateTimeOffset? DeprecatedAt);

/// <summary>An audit entry for a grant or copy change. <see cref="Kind"/> is a dotted name such as <c>hive.grant.revoked</c>.</summary>
public sealed record HiveAuditEvent(DateTimeOffset At, string Actor, string Kind, string Detail);
