namespace Bazaar.Roles.Abstractions;

/// <summary>
/// A named set of grants (doc 48 section 4). <see cref="TemplateKey"/> is set on the five built-in templates and stays when the
/// owner edits them. <see cref="Protected"/> marks the owner role, which keeps the floors of section 4.1.
/// </summary>
public sealed record Role(
    Guid Id,
    string Name,
    string Description,
    string? TemplateKey,
    bool Protected,
    IReadOnlyList<Grant> Grants,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A role held by one principal. The kind is stored so the floors can be checked without asking identity.</summary>
public sealed record RoleAssignment(Guid PrincipalId, PrincipalKind PrincipalKind, Guid RoleId, Guid AssignedBy, DateTimeOffset AssignedAt);

/// <summary>Who is making a change.</summary>
public readonly record struct RoleActor(Guid PrincipalId, PrincipalKind Kind);

/// <summary>Keys of the five built-in role templates (doc 48 section 4).</summary>
public static class RoleTemplateKeys
{
    public const string Owner = "owner";
    public const string Coworker = "coworker";
    public const string Reviewer = "reviewer";
    public const string ReadOnlyObserver = "read-only-observer";
    public const string Agent = "agent";
}
