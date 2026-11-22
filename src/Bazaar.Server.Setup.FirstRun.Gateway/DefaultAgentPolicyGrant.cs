namespace Bazaar.Server.Setup.FirstRun.Gateway;

/// <summary>
/// Writes the seeded default agent's model policy (doc 05 "Policy", doc 45 section 1.15). Deny by default: the entries are exactly the
/// whitelist the seed computed, and the default route is the route entry. A row an operator edited (any other <c>changed_by</c>)
/// is never overwritten. An empty whitelist writes nothing, so the agent stays denied.
/// </summary>
public sealed class DefaultAgentPolicyGrant(IDefaultAgentPolicyStore store)
{
    /// <summary>The changed-by label written with the seeded row, so the audit shows where it came from.</summary>
    public const string ChangedBy = "first-run-seed";

    /// <summary>Returns true when a row was written, false when nothing was written (empty whitelist or an operator-edited row).</summary>
    public async Task<bool> GrantAsync(Guid agentId, IReadOnlyList<string> whitelist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(whitelist);
        if (whitelist.Count == 0) return false;

        var id = agentId.ToString("D");
        var existing = await store.GetChangedByAsync(id, cancellationToken);
        if (existing is not null && !string.Equals(existing, ChangedBy, StringComparison.Ordinal)) return false;

        var defaultRoute = whitelist.FirstOrDefault(e => !e.Contains(':', StringComparison.Ordinal)) ?? whitelist[0];
        await store.SaveAsync(id, whitelist, defaultRoute, ChangedBy, cancellationToken);
        return true;
    }
}
