namespace Bazaar.Server.Security.Quarantine;

/// <summary>Who is acting on a quarantine record.</summary>
public enum QuarantineActorKind
{
    /// <summary>The authenticated owner. The only kind that may release.</summary>
    Owner,

    /// <summary>An authenticated co-member who is not the owner.</summary>
    MemberHuman,

    /// <summary>An agent.</summary>
    Agent,

    /// <summary>A reader session (doc 15, "Reader and actor sessions").</summary>
    Reader,

    /// <summary>Server-generated work, such as anomaly rules.</summary>
    System,
}

/// <summary>The principal behind a quarantine action.</summary>
/// <param name="Id">Principal identifier (owner id, agent id, session id, or <c>system</c>).</param>
/// <param name="Kind">Kind of principal; decides what it may do.</param>
public sealed record QuarantineActor(string Id, QuarantineActorKind Kind);
