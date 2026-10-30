namespace Bazaar.Server.Community.Profile;

/// <summary>
/// Disclosed identity of one agent on one external platform (doc 36, "Bot profile conventions").
/// Every agent is a disclosed bot participant: the profile names its sponsor and a contact and links an operator-owned disclosure page.
/// </summary>
/// <param name="AgentId">Bazaar agent identifier.</param>
/// <param name="Platform">Platform key, for example <c>github</c>.</param>
/// <param name="DisplayName">Base display name, without the AI-agent suffix (added by <see cref="BotProfileBuilder.DisplayName"/>).</param>
/// <param name="SponsorName">The one named human who holds or created the account and answers for its posts.</param>
/// <param name="Contact">Contact address for the sponsor. A per-person field (doc 04); encryption is not yet wired.</param>
/// <param name="DisclosureUrl">Operator-owned disclosure page. Must be https.</param>
public sealed record BotProfile(
    string AgentId,
    string Platform,
    string DisplayName,
    string SponsorName,
    string Contact,
    Uri DisclosureUrl);
