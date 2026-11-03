namespace Bazaar.Server.Council.Membership;

/// <summary>
/// The owner's resolution of an escalated membership vote. Its absence (null) means the owner has not resolved it.
/// </summary>
/// <param name="Choice">The owner's approve/reject choice (<see cref="ApprovalChoices"/>: yes or no).</param>
public sealed record OwnerResolution(string Choice);
