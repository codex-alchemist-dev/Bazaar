namespace Bazaar.Server.BrowserPool.Abstractions;

/// <summary>Whether a pool browser has a (virtual) display.</summary>
public enum PoolDisplayMode
{
    /// <summary>No compositor; the browser runs in its own headless mode. The default and the cheapest.</summary>
    Headless,

    /// <summary>The browser runs under a kiosk compositor with a headless backend and software rendering (doc 59 section 6).</summary>
    Headed,
}

/// <summary>
/// A request for one anonymous browser in the pool VM. There is deliberately nothing here that names an account, a profile, a
/// cookie jar or any secret: a pool session always starts from an empty profile directory (doc 59 section 7).
/// </summary>
/// <param name="AgentId">The owning agent. Letters, digits, <c>_</c> and <c>-</c>, at most 64, starting with a letter or digit.</param>
/// <param name="Display">Headless (default) or headed.</param>
/// <param name="StartUrl">Optional absolute http or https URL to open first; <c>about:blank</c> when null.</param>
/// <param name="Width">Window width in pixels, 320 to 3840.</param>
/// <param name="Height">Window height in pixels, 240 to 2160.</param>
public sealed record PoolSessionRequest(
    string AgentId,
    PoolDisplayMode Display = PoolDisplayMode.Headless,
    string? StartUrl = null,
    int Width = 1280,
    int Height = 800);

/// <summary>An open pool session.</summary>
/// <param name="SessionId">Random, unguessable id (a GUID in "N" form).</param>
/// <param name="AgentId">The owning agent.</param>
/// <param name="Slot">The pool slot; slot N runs as the OS user <c>SlotUidBase + N</c>.</param>
/// <param name="Display">Headless or headed.</param>
/// <param name="StartedAt">When the browser was started.</param>
/// <param name="ExpiresAt">Hard end of the session, whatever the activity.</param>
public sealed record PoolSession(
    string SessionId,
    string AgentId,
    int Slot,
    PoolDisplayMode Display,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A slot taken out of use because its cleanup did not complete. It counts against the pool size until a retry succeeds.</summary>
/// <param name="Slot">The slot number.</param>
/// <param name="SessionId">The session that last used it.</param>
/// <param name="AgentId">The agent that last used it.</param>
/// <param name="Since">When it was quarantined.</param>
/// <param name="Detail">Why, without any page content.</param>
public sealed record PoolQuarantinedSlot(int Slot, string SessionId, string AgentId, DateTimeOffset Since, string Detail);

/// <summary>A point-in-time view of the pool, for the admin panel and for the host's firewall refresh.</summary>
/// <param name="Capacity">Configured maximum number of sessions (quarantined slots included).</param>
/// <param name="Open">Sessions that are open now.</param>
/// <param name="Opening">Sessions that are still being started.</param>
/// <param name="Sessions">The open sessions.</param>
/// <param name="Quarantined">Slots out of use.</param>
/// <param name="FirewallRules">The per-uid egress rules that must be in force for the sessions that exist now.</param>
public sealed record PoolSnapshot(
    int Capacity,
    int Open,
    int Opening,
    IReadOnlyList<PoolSession> Sessions,
    IReadOnlyList<PoolQuarantinedSlot> Quarantined,
    IReadOnlyList<PoolFirewallRule> FirewallRules);

/// <summary>
/// The outbound rule for one slot's OS user: it may open TCP connections to its agent's egress proxy and nothing else, and the
/// pool VM enforces that outside the browser's own process (doc 16 "Network and egress").
/// </summary>
/// <param name="Uid">The slot's uid (and gid).</param>
/// <param name="ProxyHost">An IPv4 literal. Names are refused, so a firewall rule never depends on DNS.</param>
/// <param name="ProxyPort">The proxy's TCP port.</param>
public sealed record PoolFirewallRule(int Uid, string ProxyHost, int ProxyPort);
