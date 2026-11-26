using System.Net;
using System.Net.Sockets;
using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.VmEgress;

namespace Bazaar.Server.BrowserPool.Egress;

/// <summary>
/// The pool's egress policy (docs/design/59, "Browser pool isolation"; docs/design/16, WP9). It answers with the proxy address and
/// port the egress policy last stored for the agent, read through <see cref="IEgressAllowlistStore.GetAsync"/>. The stored port is the
/// bound one: the policy saves the rules only after the proxy has bound and the switch rules are in force.
/// </summary>
/// <remarks>
/// This class never decides what an agent may reach; the proxy's allowlist does, and it never starts or changes a proxy. Every path
/// answers null (no browser): no store, a store that throws, a read past <see cref="PoolEgressOptions.ReadTimeoutSeconds"/>, an agent
/// with no stored rules, an address that is not an IPv4 literal (the pool takes IPv4 literals only), a wildcard, multicast or
/// broadcast address, and a port outside 1 to 65535. Cancellation by the caller is not a verdict and propagates.
/// A stored rule set does not prove a listener is running (a restart loses the proxy, and the in-memory store loses the rules):
/// the pool's own health check is what proves a slot reaches its proxy.
/// </remarks>
public sealed class AllowlistStorePoolEgressPolicy : IPoolEgressPolicy
{
    private readonly IEgressAllowlistStore? _store;
    private readonly TimeProvider _time;
    private readonly PoolEgressOptions _options;

    /// <summary>Creates the policy. A <see langword="null"/> store means no agent has a proxy.</summary>
    public AllowlistStorePoolEgressPolicy(IEgressAllowlistStore? store, TimeProvider time, PoolEgressOptions options)
    {
        _store = store;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public async Task<PoolEgressEndpoint?> ResolveAsync(string agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_store is null || string.IsNullOrWhiteSpace(agentId))
            return null;
        EgressRules? rules;
        try
        {
            rules = await _store.GetAsync(agentId, ct)
                .WaitAsync(TimeSpan.FromSeconds(_options.ReadTimeoutSeconds), _time, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A store failure or a timeout: no answer, so no browser.
            return null;
        }

        if (rules is null || rules.ProxyPort is < 1 or > 65535)
            return null;
        IPAddress? address = rules.ProxyAddress;
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork
            || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) || address.GetAddressBytes()[0] >= 224)
            return null;
        return new PoolEgressEndpoint(address.ToString(), rules.ProxyPort);
    }
}
