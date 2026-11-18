using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using Bazaar.Server.VmEgress.Matching;
using Bazaar.Server.VmEgress.Proxy;

namespace Bazaar.Server.VmEgress;

/// <summary>
/// The egress policy (doc 15 <c>IEgressPolicy</c>, doc 16 WP9). Keeps the three layers in step: the proxy allowlist, the switch rules
/// through <see cref="IEgressEnforcer"/>, and the stored approved allowlist. Growth needs approval; a shrink applies at once.
/// </summary>
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
        return await ApplyLockedAsync(agentId, rules, seed: false, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EgressApplyResult> InitialiseAsync(string agentId, IPAddress proxyAddress, int proxyPort, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(proxyAddress);
        using var _ = await LockAsync(agentId, ct).ConfigureAwait(false);
        if (await store.GetAsync(agentId, ct).ConfigureAwait(false) is { } existing)
        {
            return await ApplyLockedAsync(agentId, existing, seed: false, ct).ConfigureAwait(false);
        }

        return await ApplyLockedAsync(agentId, new EgressRules(options.DefaultAllowlist, proxyAddress, proxyPort), seed: true, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task BlockAllAsync(string agentId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // No lock: the kill switch must not queue behind an approval round trip or a slow enforcer. The flag goes first, so an
        // Apply that is running right now sees it and shuts itself down again.
        List<Exception> failures = [];
        try
        {
            await store.SetBlockedAsync(agentId, true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            proxies.Close(agentId);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await enforcer.BlockAllAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

        try
        {
            await auditor.RuleChangeAsync(agentId, "block-all", [], null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }

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
        var rules = await store.GetAsync(agentId, ct).ConfigureAwait(false);
        if (rules is null)
        {
            // Nothing was ever approved. Leave the block in place; there is nothing to re-apply.
            return new EgressApplyResult(EgressApplyOutcome.Blocked, "No approved rules to restore.");
        }

        await store.SetBlockedAsync(agentId, false, ct).ConfigureAwait(false);
        var result = await ApplyLockedAsync(agentId, rules, seed: false, ct).ConfigureAwait(false);
        if (!result.IsApplied)
        {
            await store.SetBlockedAsync(agentId, true, ct).ConfigureAwait(false);
        }
        else
        {
            await auditor.RuleChangeAsync(agentId, "release-block", rules.AllowedDestinations, null, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<EgressApplyResult> ApplyLockedAsync(string agentId, EgressRules rules, bool seed, CancellationToken ct)
    {
        if (!Allowlist.TryCompile(rules.AllowedDestinations, out var proposed, out _))
        {
            return new EgressApplyResult(EgressApplyOutcome.Invalid, "Invalid allowlist entry.");
        }

        if (rules.ProxyPort is < 0 or > 65535)
        {
            return new EgressApplyResult(EgressApplyOutcome.Invalid, "Invalid proxy port.");
        }

        if (await store.IsBlockedAsync(agentId, ct).ConfigureAwait(false))
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

        var endpoint = proxies.StartOrUpdate(agentId, normalised);
        var bound = normalised.WithProxyPort(endpoint.Port);
        try
        {
            await enforcer.ApplyAsync(agentId, bound, ct).ConfigureAwait(false);
        }
        catch
        {
            // Fail closed: a proxy with no switch rule behind it is not a state to leave running.
            proxies.Close(agentId);
            throw;
        }

        if (await store.IsBlockedAsync(agentId, ct).ConfigureAwait(false))
        {
            // The kill switch fired while this call ran. It wins.
            proxies.Close(agentId);
            await enforcer.BlockAllAsync(agentId, ct).ConfigureAwait(false);
            return new EgressApplyResult(EgressApplyOutcome.Blocked, "Egress is blocked by the kill switch.");
        }

        await store.SaveAsync(agentId, bound, ct).ConfigureAwait(false);
        await auditor.RuleChangeAsync(agentId, change, change == "grow" ? additions : canonical, null, ct).ConfigureAwait(false);
        return new EgressApplyResult(EgressApplyOutcome.Applied);
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
