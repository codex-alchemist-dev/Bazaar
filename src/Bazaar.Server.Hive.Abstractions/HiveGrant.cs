namespace Bazaar.Server.Hive.Abstractions;

/// <summary>
/// An owner grant: lessons of <paramref name="Kind"/> from <paramref name="FromProject"/> may be copied for
/// <paramref name="ToProject"/> (doc 25, <c>hive_grants</c>). The default is no grants, so nothing transfers.
/// </summary>
/// <param name="FromProject">Source project slug.</param>
/// <param name="ToProject">Target project slug.</param>
/// <param name="Kind">The kind the grant covers.</param>
/// <param name="GrantedBy">Who granted it.</param>
public sealed record HiveGrant(string FromProject, string ToProject, HiveKind Kind, string GrantedBy);
