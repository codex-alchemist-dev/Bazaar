namespace Bazaar.Server.Gateway.Health;

/// <summary>
/// The unit health is tracked for: one provider and one of its keys (a secret reference, never the key itself).
/// Quota and health are per key, never per agent, because agents share a key.
/// </summary>
public readonly record struct ProviderKey(string ProviderId, string KeyRef);
