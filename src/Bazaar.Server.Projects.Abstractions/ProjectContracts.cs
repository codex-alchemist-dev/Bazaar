using System.Text.RegularExpressions;

namespace Bazaar.Server.Projects;

/// <summary>Lifecycle of a project (docs/design/93-projects-and-membership.md section 3). Archiving never deletes rows.</summary>
public enum ProjectState
{
    /// <summary>Members read and write.</summary>
    Active,

    /// <summary>Members keep read access, nobody gains write access.</summary>
    Archived,
}

/// <summary>Who a membership row names. Loans are not rows, so they have no kind here.</summary>
public enum ProjectPrincipalKind
{
    /// <summary>An agent.</summary>
    Agent,

    /// <summary>A human member. The owner reads every project without a row.</summary>
    User,
}

/// <summary>A member's role on a project.</summary>
public enum ProjectRole
{
    /// <summary>May add and end members.</summary>
    Lead,

    /// <summary>Ordinary member.</summary>
    Member,
}

/// <summary>Why a write was refused. Refusals change nothing.</summary>
public enum ProjectError
{
    /// <summary>The actor may not do this.</summary>
    Forbidden,

    /// <summary>The slug is not 1 to 63 characters of lowercase letters, digits and hyphens, starting with a letter or digit.</summary>
    InvalidSlug,

    /// <summary>The title is empty.</summary>
    InvalidTitle,

    /// <summary>The principal id is not a usable id (the empty GUID).</summary>
    InvalidPrincipal,

    /// <summary>No project has the slug.</summary>
    NotFound,

    /// <summary>The project is archived, so nobody gains write access.</summary>
    Archived,

    /// <summary>A project with the slug exists. Slugs are never reused.</summary>
    SlugTaken,

    /// <summary>The principal already has a current membership.</summary>
    AlreadyMember,

    /// <summary>The principal has no current membership to end.</summary>
    NotMember,
}

/// <summary>Outcome of a write: success, or the one <see cref="ProjectError"/> that refused it.</summary>
/// <param name="Error">Null on success.</param>
public sealed record ProjectWriteResult(ProjectError? Error)
{
    /// <summary>True when the write was applied and audited.</summary>
    public bool Succeeded => Error is null;

    /// <summary>The success value.</summary>
    public static ProjectWriteResult Ok { get; } = new((ProjectError?)null);

    /// <summary>A refusal.</summary>
    public static ProjectWriteResult Refused(ProjectError error) => new(error);
}

/// <summary>
/// Who is writing. Create needs the owner. Adding and ending members needs the owner or a current lead of the project
/// (the <c>lead_and_owner</c> default of <c>projects.member_adders</c>). Archive follows the same rule.
/// </summary>
public sealed record ProjectActor
{
    private ProjectActor(string? kind, Guid? id) => (Kind, Id) = (kind, id);

    /// <summary>"owner", "agent" or "user". Null never occurs; the owner is "owner".</summary>
    public string? Kind { get; }

    /// <summary>The agent or user id; null for the owner.</summary>
    public Guid? Id { get; }

    /// <summary>The owner.</summary>
    public static ProjectActor Owner() => new("owner", null);

    /// <summary>An agent acting on its own authority.</summary>
    public static ProjectActor Agent(Guid agentId) => new("agent", agentId);

    /// <summary>A human member.</summary>
    public static ProjectActor User(Guid userId) => new("user", userId);

    /// <summary>The actor label audit entries and <c>created_by</c>/<c>added_by</c> columns carry.</summary>
    public string Label => Id is { } id ? $"{Kind}:{id:D}" : "owner";
}

/// <summary>One project row.</summary>
public sealed record ProjectRecord(Guid Id, string Slug, string Title, ProjectState State, string CreatedBy, DateTimeOffset CreatedAt, long Version);

/// <summary>One membership row (not a loan).</summary>
public sealed record ProjectMemberRecord(
    Guid ProjectId,
    ProjectPrincipalKind PrincipalKind,
    Guid PrincipalId,
    ProjectRole Role,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    string AddedBy,
    long Version);

/// <summary>The slug rule of doc 93 section 3.</summary>
public static partial class ProjectSlug
{
    /// <summary>True for a slug that matches <c>^[a-z0-9][a-z0-9-]{0,62}$</c>.</summary>
    public static bool IsValid(string? slug) => slug is not null && slug.Length <= 63 && Pattern().IsMatch(slug);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();
}

/// <summary>
/// The write API of the Projects module (doc 93 section 5). Each success is applied and audited in one transaction
/// with actor, diff and time. Membership reads go through <c>IProjectMembership</c> in Bazaar.Server.
/// </summary>
public interface IProjectStore
{
    /// <summary>Creates an active project. Owner only (<c>projects.creators</c> floor).</summary>
    Task<ProjectWriteResult> CreateAsync(ProjectActor actor, string slug, string title, CancellationToken ct = default);

    /// <summary>Adds a member, or re-adds one whose membership ended. Refused on an archived project.</summary>
    Task<ProjectWriteResult> AddMemberAsync(
        ProjectActor actor, string slug, ProjectPrincipalKind kind, Guid principalId, ProjectRole role, CancellationToken ct = default);

    /// <summary>Ends a current membership now. The row stays as history.</summary>
    Task<ProjectWriteResult> EndMembershipAsync(
        ProjectActor actor, string slug, ProjectPrincipalKind kind, Guid principalId, CancellationToken ct = default);

    /// <summary>Archives an active project. Members keep read access; no rows are deleted. Archived is final.</summary>
    Task<ProjectWriteResult> ArchiveAsync(ProjectActor actor, string slug, CancellationToken ct = default);

    /// <summary>The project with the slug, or null.</summary>
    Task<ProjectRecord?> GetAsync(string slug, CancellationToken ct = default);

    /// <summary>Every membership row of the project, current and ended, oldest first. Empty for an unknown slug.</summary>
    Task<IReadOnlyList<ProjectMemberRecord>> ListMembersAsync(string slug, CancellationToken ct = default);
}
