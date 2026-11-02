namespace Bazaar.Server.Vault.Search;

/// <summary>
/// The access rule for Vault reads, search hits and link resolution (doc 18, "Access"). The SQL in
/// <see cref="Data.VaultSearchRepository"/> applies the same rule before ranking.
/// </summary>
public static class VaultAccess
{
    /// <summary>
    /// Shared and company documents (no owner) are readable by everyone who can reach the Vault. A private document
    /// (an agent's memory or charter) is readable by the owner and by that agent only.
    /// </summary>
    public static bool CanRead(string? documentOwner, VaultCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.IsOwner || documentOwner is null)
        {
            return true;
        }

        return caller.AgentOwner is not null && string.Equals(documentOwner, caller.AgentOwner, StringComparison.Ordinal);
    }
}
