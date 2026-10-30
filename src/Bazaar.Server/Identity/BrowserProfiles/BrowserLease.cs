using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.BrowserProfiles;

/// <summary>
/// The lease that authorises one injection (doc 14, "Release modes"): one agent, one service, one lease, valid until
/// <see cref="ExpiresAt"/>. Issued by the access flow; this type only carries what injection checks.
/// </summary>
public sealed record BrowserLease(LeaseId Lease, string AgentId, string Service, DateTimeOffset ExpiresAt);
