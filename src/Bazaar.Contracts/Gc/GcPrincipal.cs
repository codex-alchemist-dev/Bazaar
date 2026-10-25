namespace Bazaar.Contracts.Gc;

/// <summary>A person or agent that can read and write GCs.</summary>
/// <param name="Id">Stable principal id.</param>
/// <param name="Kind">Owner, human or agent.</param>
/// <param name="DisplayName">Name shown in messages and profile cards.</param>
public sealed record GcPrincipal(Guid Id, PrincipalKind Kind, string DisplayName);
