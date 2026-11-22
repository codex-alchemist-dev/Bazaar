using System.Text.RegularExpressions;

namespace Bazaar.Tabula;

/// <summary>Why access was granted (doc 81 section 9). Required on every grant.</summary>
public enum AccessGrantReason
{
    Support,
    Incident,
    Legal,
    Recovery,
    Maintenance,
    Review,
    Investigation,
    Other,
}

/// <summary>
/// Reason-tagged access-grant event. Free-text reasons never enter audit; only <see cref="ReasonTag"/> and <see cref="ReasonLabel"/> do.
/// Principals are canonical text (<c>kind:id</c>).
/// </summary>
public sealed partial record AccessGrantEvent(
    Guid GrantId,
    string Grantor,
    string Grantee,
    string? Subject,
    string ScopeKind,
    string ScopeId,
    AccessGrantReason ReasonTag,
    string ReasonLabel,
    DateTimeOffset ExpiresAt,
    Guid? ApprovalId)
{
    /// <summary>Operation kinds the event family registers (doc 81 section 9): all Security, Privileged and RetentionExempt.</summary>
    public static IReadOnlyList<string> OperationKinds { get; } =
        ["access_grant.issued", "access_grant.used", "access_grant.denied", "access_grant.revoked", "access_grant.expired"];

    /// <summary>Throws when the event could not be a real grant: no reason, no future expiry, or blank parties.</summary>
    public void Validate(DateTimeOffset now)
    {
        if (!Enum.IsDefined(ReasonTag))
        {
            throw new ArgumentException("A grant needs a defined reason tag.");
        }

        if (string.IsNullOrWhiteSpace(Grantor) || string.IsNullOrWhiteSpace(Grantee) ||
            string.IsNullOrWhiteSpace(ScopeKind) || string.IsNullOrWhiteSpace(ScopeId))
        {
            throw new ArgumentException("Grantor, grantee and scope are required.");
        }

        if (ReasonLabel is null || !LabelPattern().IsMatch(ReasonLabel))
        {
            throw new ArgumentException("Reason label must be a slug matching ^[a-z][a-z0-9_]{0,31}$.");
        }

        if (ExpiresAt <= now)
        {
            throw new ArgumentException("A grant needs an expiry in the future.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();
}
