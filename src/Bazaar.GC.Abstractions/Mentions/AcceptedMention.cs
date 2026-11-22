namespace Bazaar.Server.Gc.Mentions;

/// <summary>A span that passed validation and will produce a <c>gc_mention</c> row.</summary>
/// <param name="TargetPrincipalId">The validated target.</param>
/// <param name="Start">UTF-16 offset, copied from the span.</param>
/// <param name="Length">UTF-16 length, copied from the span.</param>
public sealed record AcceptedMention(Guid TargetPrincipalId, int Start, int Length);
