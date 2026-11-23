namespace Bazaar.Server.Hive.Abstractions;

/// <summary>
/// The authenticated caller of a Hive wire request, supplied by the host adapter from the session.
/// <see cref="Principal"/> is recorded as the grantor; <see cref="IsOwner"/> gates both grant request kinds.
/// </summary>
/// <param name="Principal">Who is asking, recorded as the grantor or revoker.</param>
/// <param name="IsOwner">True only when the session is the owner's.</param>
public sealed record HiveCaller(string Principal, bool IsOwner);
