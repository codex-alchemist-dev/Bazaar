namespace Bazaar.Client.Core.Souls;

/// <summary>
/// Identity fields the profile card shows for one Soul. The server builds it from the agent row; the card
/// only renders it (docs/design/07-soul-system.md, Soul record parts; 27-client-architecture.md, GC message anatomy).
/// </summary>
/// <param name="SoulId">The Soul this profile describes.</param>
/// <param name="Name">Display name.</param>
/// <param name="Role">Role title, such as "Architect" or "Developer".</param>
/// <param name="ModelBadge">Short model label for the badge, such as "Haiku 5.5". Empty when none is set.</param>
public sealed record SoulProfile(Guid SoulId, string Name, string Role, string ModelBadge);
