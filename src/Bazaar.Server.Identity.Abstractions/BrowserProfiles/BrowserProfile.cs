namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// One browser profile owned by one agent. For account profiles, <see cref="ServiceAllowlist"/> lists the only sites
/// whose cookies may be stored, materialised or injected. Anonymous profiles have an empty allowlist.
/// </summary>
public sealed record BrowserProfile(
    Guid ProfileId,
    string AgentId,
    BrowserProfileKind Kind,
    IReadOnlyList<string> ServiceAllowlist);
