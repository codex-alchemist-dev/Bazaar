namespace Bazaar.Server.VmEgress;

/// <summary>A plain HTTP request on its way upstream, after the allowlist and address checks passed. Headers can be changed.</summary>
public sealed class ProxyRequestContext(string agentId, string host, int port, string method, string pathAndQuery, List<KeyValuePair<string, string>> headers)
{
    /// <summary>Agent whose proxy carries the request.</summary>
    public string AgentId { get; } = agentId;

    /// <summary>Normalised target host (lower case, ASCII form).</summary>
    public string Host { get; } = host;

    /// <summary>Target port.</summary>
    public int Port { get; } = port;

    /// <summary>HTTP method.</summary>
    public string Method { get; } = method;

    /// <summary>Request target in origin form.</summary>
    public string PathAndQuery { get; } = pathAndQuery;

    /// <summary>The request headers that will be sent, without hop-by-hop headers. Host is set by the proxy and ignored here.</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = headers;

    /// <summary>Set to refuse the request; the client receives 403.</summary>
    public bool Rejected { get; set; }
}

/// <summary>
/// Hook for work on plain HTTP requests before they leave the host, such as cookie injection from the credential broker (doc 15).
/// No implementation ships in this module. It cannot see inside a CONNECT tunnel.
/// </summary>
public interface IProxyRequestFilter
{
    /// <summary>Called for each plain HTTP request. May change headers or set <see cref="ProxyRequestContext.Rejected"/>.</summary>
    ValueTask FilterAsync(ProxyRequestContext context, CancellationToken ct);
}
