using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using Bazaar.Server.VmEgress.Matching;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// The egress policy (doc 15 <c>IEgressPolicy</c>, doc 16 WP9). Keeps the three layers in step: the proxy allowlist, the switch rules
/// through <see cref="IEgressEnforcer"/>, and the stored approved allowlist. Growth needs approval; a shrink applies at once.
/// </summary>
/// <remarks>
/// Every path that closes egress (the kill switch, and the clean-up after a failed or interrupted apply) uses
/// <see cref="CancellationToken.None"/>, so a cancelled caller can never leave a proxy or switch rule open. Residual: the
/// enforcer and the stored block flag are separate systems, so a crash between them is resolved by the next block or apply.
/// </remarks>
public sealed class EgressPolicyService(
    IEgressAllowlistStore store,
    IEgressEnforcer enforcer,
    IEgressProxyHost proxies,
    IEgressApprovalGate gate,
    EgressAuditor auditor,
    VmEgressOptions options) : IEgressPolicy
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<EgressApplyResult> ApplyAsync(string agentId, EgressRules rules, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(rules);
        using var _ = await LockAsync(agentId, ct).ConfigureAwait(false);
        return await ApplyLockedAsync(agentId, rules, seed: false, releaseEpoch: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EgressApplyResult> InitialiseAsync(string agentId, IPAddress proxyAddress, int proxyPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(proxyAddress);
        using var _ = await LockAsync(agentId, ct).ConfigureAwait(false);
        if (await store.GetAsync(agentId, ct).ConfigureAwait(false) is { } existing)
        {
            return await ApplyLockedAsync(agentId, existing, seed: false, releaseEpoch: null, ct).ConfigureAwait(false);
        }

        return await ApplyLockedAsync(agentId, new EgressRules(options.DefaultAllowlist, proxyAddress, proxyPort), seed: true, releaseEpoch: null, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>The <paramref name="ct"/> is ignored on purpose: a kill switch that can be cancelled is not a kill switch.</remarks>
    public async Task BlockAllAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        _ = ct;
        var none = CancellationToken.None;

        // No lock: the kill switch must not queue behind an approval round trip or a slow enforcer. The flag and epoch go first,
        // so an Apply or Release that is running right now sees them and shuts itself down again.
        List<Exception> failures = [];
        Try(() => proxies.Block(agentId), failures);
        await TryAsync(() => store.BlockAsync(agentId, none), failures).ConfigureAwait(false);
        await TryAsync(() => enforcer.BlockAllAsync(agentId, none), failures).ConfigureAwait(false);
        await TryAsync(() => auditor.RuleChangeAsync(agentId, "block-all", [], null, none), failures).ConfigureAwait(false);

        if (failures.Count > 0)
        {
            throw new AggregateException("Blocking the agent's egress did not complete cleanly. Treat egress as not blocked until it is retried.", failures);
        }
    }

    /// <inheritdoc />
    public async Task<EgressApplyResult> ReleaseBlockAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        using var _ = await LockAsync(agentId, ct).ConfigureAwait(false);
        if (!await store.IsBlockedAsync(agentId, ct).ConfigureAwait(false))
        {
            return new EgressApplyResult(EgressApplyOutcome.Applied, "Egress was not blocked.");
        }

        var rules = await store.GetAsync(agentId, ct).ConfigureAwait(false);
        if (rules is null)
        {
            // Nothing was ever approved, so there is nothing to re-apply. Egress stays default-deny (the proxy and the switch rule are
            // already closed); only the block flag is cleared, by compare-and-set so a block that arrives meanwhile still wins.
            var seen = await store.GetBlockEpochAsync(agentId, ct).ConfigureAwait(false);
            if (!await store.TryReleaseBlockAsync(agentId, seen, ct).ConfigureAwait(false))
            {
                return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress was blocked again while the release ran.");
            }

            return new EgressApplyResult(EgressApplyOutcome.Applied, "No approved rules to restore; egress stays default-deny.");
        }

        // The block is cleared only by compare-and-set on the epoch read here, after the rules are back. A block that arrives in
        // between raises the epoch, the clear fails, and the apply shuts itself down again.
        var epoch = await store.GetBlockEpochAsync(agentId, ct).ConfigureAwait(false);
        var result = await ApplyLockedAsync(agentId, rules, seed: false, releaseEpoch: epoch, ct).ConfigureAwait(false);
        if (result.IsApplied)
        {
            await auditor.RuleChangeAsync(agentId, "release-block", rules.AllowedDestinations, null, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<EgressApplyResult> ApplyLockedAsync(string agentId, EgressRules rules, bool seed, long? releaseEpoch, CancellationToken ct)
    {
        if (!Allowlist.TryCompile(rules.AllowedDestinations, out var proposed, out _))
        {
            return new EgressApplyResult(EgressApplyOutcome.Invalid, "Invalid allowlist entry.");
        }

        if (rules.ProxyPort is < 0 or > 65535 || !IsUsableProxyAddress(rules.ProxyAddress))
        {
            return new EgressApplyResult(EgressApplyOutcome.Invalid, "Invalid proxy address or port.");
        }

        if (releaseEpoch is null && await store.IsBlockedAsync(agentId, ct).ConfigureAwait(false))
        {
            return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress is blocked by the kill switch.");
        }

        var current = await store.GetAsync(agentId, ct).ConfigureAwait(false);
        var currentList = current is null ? Allowlist.Empty : Allowlist.TryCompile(current.AllowedDestinations, out var c, out _) ? c : Allowlist.Empty;
        var additions = seed ? [] : currentList.Additions(proposed);

        var change = seed ? "seed" : additions.Count > 0 ? "grow" : "shrink-or-same";
        if (additions.Count > 0)
        {
            var state = await gate.RequestGrowthAsync(agentId, additions, options.EffectiveApprovalTier, ct).ConfigureAwait(false);
            if (state != EgressApprovalState.Approved)
            {
                await auditor.RuleChangeAsync(
                    agentId,
                    state == EgressApprovalState.Pending ? "grow-pending" : "grow-denied",
                    additions,
                    null,
                    ct).ConfigureAwait(false);
                return state == EgressApprovalState.Pending
                    ? new EgressApplyResult(EgressApplyOutcome.ApprovalPending, "Waiting for the owner to approve the new destinations.")
                    : new EgressApplyResult(EgressApplyOutcome.ApprovalDenied, "The new destinations were not approved.");
            }
        }

        // Same entries, normalised: what the proxy enforces is what is stored and audited.
        var canonical = ImmutableArray.CreateRange(proposed.Entries.Select(e => e.Canonical));
        var normalised = new EgressRules(canonical, rules.ProxyAddress, rules.ProxyPort);

        // From here on, anything that goes wrong or is interrupted closes egress. A half-applied state is never left open.
        try
        {
            if (releaseEpoch is not null)
            {
                proxies.Unblock(agentId);
            }

            var endpoint = proxies.StartOrUpdate(agentId, normalised);
            var bound = normalised.WithProxyPort(endpoint.Port);
            await enforcer.ApplyAsync(agentId, bound, ct).ConfigureAwait(false);

            if (releaseEpoch is { } epoch)
            {
                if (!await store.TryReleaseBlockAsync(agentId, epoch, ct).ConfigureAwait(false))
                {
                    await CloseEverythingAsync(agentId, releaseEpoch is not null).ConfigureAwait(false);
                    return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress was blocked again while the release ran.");
                }
            }
            else if (await store.IsBlockedAsync(agentId, ct).ConfigureAwait(false))
            {
                // The kill switch fired while this call ran. It wins.
                await CloseEverythingAsync(agentId, releaseEpoch is not null).ConfigureAwait(false);
                return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress is blocked by the kill switch.");
            }

            await store.SaveAsync(agentId, bound, ct).ConfigureAwait(false);
            await auditor.RuleChangeAsync(agentId, change, change == "grow" ? additions : canonical, null, ct).ConfigureAwait(false);
            return new EgressApplyResult(EgressApplyOutcome.Applied);
        }
        catch (EgressBlockedException)
        {
            await CloseEverythingAsync(agentId, releaseEpoch is not null).ConfigureAwait(false);
            return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress is blocked by the kill switch.");
        }
        catch
        {
            await CloseEverythingAsync(agentId, releaseEpoch is not null).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Closes the proxy and drops the switch allow rule. Never cancelled, and never throws: the caller is already failing closed.</summary>
    private async Task CloseEverythingAsync(string agentId, bool reblock)
    {
        try
        {
            if (reblock)
            {
                proxies.Block(agentId);
            }
            else
            {
                proxies.Close(agentId);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Closing again below and the original failure are what the caller sees.
        }

        try
        {
            await enforcer.BlockAllAsync(agentId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The enforcer is unreachable. The proxy is closed, which is the layer this module controls.
        }
    }

    private static bool IsUsableProxyAddress(IPAddress address) =>
        address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !address.Equals(IPAddress.Any)
        && !address.Equals(IPAddress.IPv6Any)
        && !address.Equals(IPAddress.Broadcast)
        && !(address.AddressFamily == AddressFamily.InterNetworkV6 ? address.IsIPv6Multicast : address.GetAddressBytes()[0] >= 224);

    private static void Try(Action action, List<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static async Task TryAsync(Func<Task> action, List<Exception> failures)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private async Task<IDisposable> LockAsync(string agentId, CancellationToken ct)
    {
        var gateFor = _locks.GetOrAdd(agentId, _ => new SemaphoreSlim(1, 1));
        await gateFor.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(gateFor);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
