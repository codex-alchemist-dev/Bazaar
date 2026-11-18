namespace Bazaar.Client.Hive.Grants;

/// <summary>
/// One grant in the owner matrix: copies of <paramref name="Kind"/> lessons may flow from <paramref name="FromProject"/>
/// into <paramref name="ToProject"/> (doc 08 §6, doc 25). <paramref name="GrantedBy"/> is the owner or a delegate.
/// </summary>
public sealed record HiveGrant(string FromProject, string ToProject, string Kind, string GrantedBy)
{
    /// <summary>The matrix cell this grant fills.</summary>
    public HiveGrantKey Key => new(FromProject, ToProject, Kind);
}

/// <summary>The identity of one grant: (from project, to project, kind). Grants are never written by the pane.</summary>
public sealed record HiveGrantKey(string FromProject, string ToProject, string Kind);

/// <summary>The server's answer to a grant or revoke request. <see cref="Reason"/> is empty when accepted.</summary>
public sealed record HiveGrantResult(bool Accepted, string Reason);
