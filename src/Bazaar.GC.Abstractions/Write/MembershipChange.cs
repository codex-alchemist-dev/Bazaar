namespace Bazaar.Server.Gc.Write;

/// <summary>
/// The membership state an event writes: a join or a leave. Carries primitives only, so the abstractions project does not
/// depend on the server's model. The writer sets the joined and left seqs from the event's own seq.
/// </summary>
/// <param name="PrincipalId">The principal whose membership changes.</param>
/// <param name="Role">The role text (<c>member</c> or <c>observer</c>), kept as the stored value.</param>
/// <param name="Leaves">True when this event removes the principal from the GC; false for a join.</param>
public sealed record MembershipChange(Guid PrincipalId, string Role, bool Leaves);
