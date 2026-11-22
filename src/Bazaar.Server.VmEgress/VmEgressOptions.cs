using System.Collections.Immutable;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// Operator settings for egress (doc 15 <c>security.egress.default_allowlist</c>, doc 16 <c>vm.egress.allowlist_change_approval</c>).
/// The limits below are safe shipped defaults. Nothing here can loosen the address refusals or the default-deny.
/// </summary>
public sealed record VmEgressOptions
{
    /// <summary>Lowest approval tier for allowlist growth (doc 16 floor: Tier 2). A lower value is raised to the floor.</summary>
    public const int ApprovalTierFloor = 2;

    /// <summary>
    /// <c>security.egress.default_allowlist</c>: seeded into an agent's first allowlist, no approval request (the owner approved it
    /// at setup). The shipped default is empty; setup fills in the package feed, GitHub API and model gateway entries.
    /// </summary>
    public ImmutableArray<string> DefaultAllowlist { get; init; } = [];

    /// <summary><c>vm.egress.allowlist_change_approval</c> as a tier number. Values below <see cref="ApprovalTierFloor"/> act as the floor.</summary>
    public int AllowlistChangeApprovalTier { get; init; } = ApprovalTierFloor;

    /// <summary>Tier actually used.</summary>
    public int EffectiveApprovalTier => Math.Max(AllowlistChangeApprovalTier, ApprovalTierFloor);

    /// <summary>Most simultaneous client connections one agent's proxy serves. More are closed at once.</summary>
    public int MaxConnectionsPerAgent { get; init; } = 64;

    /// <summary>Largest request head (request line and headers) in bytes. A larger one is refused with 431.</summary>
    public int MaxHeaderBytes { get; init; } = 16 * 1024;

    /// <summary>Most header lines in one request.</summary>
    public int MaxHeaderCount { get; init; } = 100;

    /// <summary>Time a client has to send a complete request head.</summary>
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Time allowed to connect upstream.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A tunnel or request with no bytes in either direction for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Audit rows of one kind (allow or deny) per agent in one window. Further events are counted and summarised.</summary>
    public int AuditRowsPerWindow { get; init; } = 20;

    /// <summary>Length of the audit rate window.</summary>
    public TimeSpan AuditWindow { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Checks ranges. Throws on a value that would disable a limit.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConnectionsPerAgent, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeaderBytes, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxHeaderCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(AuditRowsPerWindow, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HeaderTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(AuditWindow, TimeSpan.Zero);
    }
}
