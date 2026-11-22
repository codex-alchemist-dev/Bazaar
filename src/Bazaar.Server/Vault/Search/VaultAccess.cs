namespace Bazaar.Server.Vault.Search;

/// <summary>
/// The access rule for Vault reads, search hits and link resolution (doc 18, "Access"). The SQL in
/// <see cref="Data.VaultSearchRepository"/> applies the same rule before ranking. It fails closed: a document no rule
/// names is readable by the owner only.
/// </summary>
public static class VaultAccess
{
    /// <summary>The path prefixes doc 18 makes readable by every agent: <c>company/</c> and the shared areas.</summary>
    public static IReadOnlyList<string> AgentReadablePrefixes { get; } = ["company/", "shared/"];

    private const string _agentOwnerPrefix = "agent:";

    /// <summary>
    /// The owner reads everything. A document with an <paramref name="documentOwner"/> (an agent's private memory) is
    /// readable by that agent only. A document with no owner is readable by an agent only through an explicit area rule:
    /// <see cref="AgentReadablePrefixes"/>, or the agent's own <c>agents/&lt;id&gt;/</c> area. Everything else with no
    /// owner (project areas, <c>hive/</c>, <c>decisions/</c>, drafts, root files) is the owner's only until the caller
    /// carries project membership and Hive grants. Callers that are neither the owner nor an agent read nothing.
    /// </summary>
    public static bool CanRead(string path, string? documentOwner, VaultCaller caller)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsOwner)
        {
            return true;
        }

        if (caller.AgentOwner is null)
        {
            return false;
        }

        if (documentOwner is not null)
        {
            return string.Equals(documentOwner, caller.AgentOwner, StringComparison.Ordinal);
        }

        foreach (var prefix in AgentReadablePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return caller.AgentOwner.StartsWith(_agentOwnerPrefix, StringComparison.Ordinal)
            && path.StartsWith("agents/" + caller.AgentOwner[_agentOwnerPrefix.Length..] + "/", StringComparison.Ordinal);
    }
}
