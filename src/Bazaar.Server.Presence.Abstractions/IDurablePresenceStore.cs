namespace Bazaar.Server.Presence.Abstractions;

/// <summary>
/// A presence store that survives a restart (doc 23: Postgres <c>agent_activity</c> is the truth). When one is registered, the
/// presence module uses it in place of the in-memory store and layers the cache snapshot store on top.
/// </summary>
public interface IDurablePresenceStore : IPresenceStore;
