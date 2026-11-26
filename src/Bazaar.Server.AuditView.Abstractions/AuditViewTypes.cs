using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.AuditView;

/// <summary>Who asks to view the audit log (doc 48 section 5.4).</summary>
public readonly record struct AuditViewer(Guid PrincipalId, PrincipalKind Kind);

/// <summary>Names of the built-in event-kind categories. The operator can add more in the category map.</summary>
public static class AuditViewCategories
{
    public const string Security = "security";

    /// <summary>Kinds that match no category prefix. Only a viewer who may see everything reads these.</summary>
    public const string Other = "other";
}

/// <summary>
/// What a viewer may see: everything, or the listed categories, or nothing. <see cref="Denied"/> is the fail-closed default.
/// </summary>
public sealed record AuditViewScope(bool All, IReadOnlySet<string> Categories)
{
    public static AuditViewScope Denied { get; } = new(false, new HashSet<string>(StringComparer.Ordinal));

    public static AuditViewScope Everything { get; } = new(true, new HashSet<string>(StringComparer.Ordinal));

    public bool IsDenied => !All && Categories.Count == 0;

    public bool Allows(string category) => All || Categories.Contains(category);
}

/// <summary>
/// A page request. A kind filter entry matches a kind exactly, or as a prefix when it ends with a dot (<c>security.</c>).
/// The cursor is the opaque value from the previous page.
/// </summary>
public sealed record AuditViewQuery(
    long? FromSeq = null,
    long? ToSeq = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? Kinds = null,
    string? Cursor = null,
    int? PageSize = null);

/// <summary>
/// One row as shown. <see cref="Payload"/> is the stored text, so a personal field that is encrypted stays ciphertext: the
/// view never decrypts (doc 48 section 5.5). Hashes are not part of a view row.
/// </summary>
public sealed record AuditViewRow(long Seq, DateTimeOffset Ts, string Actor, string Kind, string Category, string Payload);

public enum AuditViewAccess
{
    Granted,
    Denied,
    InvalidQuery,
}

/// <summary>One page. <see cref="NextCursor"/> is null at the end of the log.</summary>
public sealed record AuditViewPage(AuditViewAccess Access, IReadOnlyList<AuditViewRow> Rows, string? NextCursor)
{
    public static AuditViewPage Refused(AuditViewAccess access) => new(access, [], null);
}

/// <summary>A raw row from the source, as stored.</summary>
public sealed record AuditSourceRow(long Seq, DateTimeOffset Ts, string Actor, string Kind, string Payload);

/// <summary>A read of the chain in sequence order: rows with <c>Seq</c> greater than <see cref="AfterSeq"/>, at most <see cref="Limit"/>.</summary>
public sealed record AuditSourceQuery(long AfterSeq, long? ToSeq, DateTimeOffset? From, DateTimeOffset? To, int Limit);
