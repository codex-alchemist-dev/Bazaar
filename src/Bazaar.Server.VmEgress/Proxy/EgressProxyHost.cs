using System.Collections.Concurrent;
using System.Net;
using Bazaar.Server.VmEgress.Matching;

namespace Bazaar.Server.VmEgress.Proxy;

/// <summary>Runs one <see cref="AgentEgressProxy"/> per agent.</summary>
public interface IEgressProxyHost
{
    /// <summary>Starts the agent's proxy, or updates its allowlist when it is running. Returns the bound endpoint.</summary>
    IPEndPoint StartOrUpdate(string agentId, EgressRules rules);

    /// <summary>Closes the agent's listener and all its connections. Idempotent.</summary>
    void Close(string agentId);
}

/// <summary>The in-process proxy host. Proxies are created on demand and replaced after a stop.</summary>
public sealed class EgressProxyHost(VmEgressOptions options, EgressAuditor auditor, IEnumerable<IProxyRequestFilter>? filters = null) : IEgressProxyHost, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentEgressProxy> _proxies = new(StringComparer.Ordinal);
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
            if (_proxies.TryGetValue(agentId, out var running) && !running.IsStopped)
            {
                running.UpdateAllowlist(allowlist);
                return running.LocalEndPoint!;
            }

            // A restart keeps the port the guest and the switch rule already know, unless the rules ask for another.
            var port = rules.ProxyPort != 0 ? rules.ProxyPort : running?.LocalEndPoint?.Port ?? 0;
            var proxy = new AgentEgressProxy(agentId, rules.ProxyAddress, port, allowlist, options, auditor, _filters);
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
