namespace Bazaar.Server.Transports;

/// <summary>
/// What a client receives for one way to reach the server. Carries no credentials and no principal names.
/// Doc 39 section 1 and the offer frame in its data section.
/// </summary>
/// <param name="ModuleId">Stable module id, for example <c>lan-mdns</c>.</param>
/// <param name="Scope">Reach of the path.</param>
/// <param name="Uri">Endpoint. <c>wss</c> unless the scope is <see cref="TransportScope.Loopback"/>.</param>
/// <param name="ServerKeyHash">Server key hash the client pins (doc 32, section 2), in <c>sha256:&lt;hex&gt;</c> form.</param>
/// <param name="ExpiresAt">When the address stops being valid, or null when it is stable.</param>
/// <param name="RequiresRelay">True when traffic passes through an operator relay.</param>
/// <param name="Verified">True when the module confirmed reachability from outside, not just from configuration.</param>
public sealed record TransportOffer(
    string ModuleId,
    TransportScope Scope,
    Uri Uri,
    string ServerKeyHash,
    DateTimeOffset? ExpiresAt,
    bool RequiresRelay,
    bool Verified);
