using System.Collections.Concurrent;
using System.Net;
using Bazaar.Server.VmEgress.Matching;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Thrown by <see cref="IEgressProxyHost.StartOrUpdate"/> for an agent whose egress is blocked.</summary>
public sealed class EgressBlockedException(string agentId) : InvalidOperationException("Egress is blocked for this agent: " + agentId)
{
}

/// <summary>Runs one <see cref="AgentEgressProxy"/> per agent.</summary>
public interface IEgressProxyHost
{
    /// <summary>
    /// Starts the agent's proxy, or updates its allowlist when it is running. Returns the bound endpoint. A changed proxy address
    /// or port restarts the listener. Throws <see cref="EgressBlockedException"/> while the agent is blocked.
    /// </summary>
    IPEndPoint StartOrUpdate(string agentId, EgressRules rules);

    /// <summary>Closes the agent's listener and all its connections. Idempotent. A later start is allowed.</summary>
    void Close(string agentId);

    /// <summary>Kill switch: closes everything like <see cref="Close"/> and refuses any start until <see cref="Unblock"/>.</summary>
    void Block(string agentId);

    /// <summary>Lets <see cref="StartOrUpdate"/> start the agent's proxy again.</summary>
    void Unblock(string agentId);
}

/// <summary>The in-process proxy host. Proxies are created on demand and replaced after a stop.</summary>
public sealed class EgressProxyHost(VmEgressOptions options, EgressAuditor auditor, IEnumerable<IProxyRequestFilter>? filters = null) : IEgressProxyHost, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentEgressProxy> _proxies = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<IProxyRequestFilter> _filters = filters?.ToList() ?? [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public IPEndPoint StartOrUpdate(string agentId, EgressRules rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!Allowlist.TryCompile(rules.AllowedDestinations, out var allowlist, out var bad))
        {
            throw new ArgumentException($"Invalid allowlist entry: {bad}", nameof(rules));
        }

        lock (_gate)
        {
            if (_blocked.Contains(agentId))
            {
                throw new EgressBlockedException(agentId);
            }

            _proxies.TryGetValue(agentId, out var running);
            if (running is { IsStopped: false } live)
            {
                var bound = live.LocalEndPoint!;
                var sameAddress = live.ListenAddress.Equals(rules.ProxyAddress);
                var samePort = rules.ProxyPort == 0 || rules.ProxyPort == bound.Port;
                if (sameAddress && samePort)
                {
                    live.UpdateAllowlist(allowlist);
                    return bound;
                }

                // The address or port the guest is allowed to reach changed, so the old listener must go.
                live.Stop();
                running = null;
            }

            var proxy = new AgentEgressProxy(agentId, rules.ProxyAddress, rules.ProxyPort, allowlist, options, auditor, _filters);
            var endpoint = proxy.Start();
            _proxies[agentId] = proxy;
            return endpoint;
        }
    }

    /// <inheritdoc />
    public void Close(string agentId)
    {
        if (_proxies.TryGetValue(agentId, out var proxy))
        {
            proxy.Stop();
        }
    }

    /// <inheritdoc />
    public void Block(string agentId)
    {
        lock (_gate)
        {
            _blocked.Add(agentId);
        }

        Close(agentId);
    }

    /// <inheritdoc />
    public void Unblock(string agentId)
    {
        lock (_gate)
        {
            _blocked.Remove(agentId);
        }
    }

    /// <summary>The agent's running proxy, for diagnostics and tests.</summary>
    public AgentEgressProxy? Get(string agentId) => _proxies.TryGetValue(agentId, out var p) ? p : null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var proxy in _proxies.Values)
        {
            await proxy.DisposeAsync().ConfigureAwait(false);
        }
    }
}
