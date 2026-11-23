using System.Security.Cryptography;
using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Leases;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Security.Credentials;

/// <summary>
/// Keeps <see cref="InMemoryLeasedCredentialMatcher"/> in step with credential leases (doc 15, "Reads for modules"): a value
/// is tracked when its lease is issued and released when the lease is revoked. Registered as an <see cref="ILeaseObserver"/>.
/// </summary>
/// <remarks>
/// A lease whose value cannot be resolved is not tracked, and the miss is logged without the value or its credential id's
/// content beyond the lease id. The revocation scope carries no lease ids, so an agent revocation releases every lease of
/// that agent and a global revocation releases all of them.
/// </remarks>
public sealed partial class LeasedCredentialObserver(
    InMemoryLeasedCredentialMatcher matcher,
    ILeasedCredentialValueSource values,
    ILogger<LeasedCredentialObserver> logger) : ILeaseObserver
{
    /// <inheritdoc />
    public async Task OnIssuedAsync(CredentialLease lease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var value = await values.ResolveAsync(lease.AgentId, lease.CredentialId, ct).ConfigureAwait(false);
        if (value is null || value.Length == 0)
        {
            LogUnresolved(logger, lease.Id.Value);
            return;
        }

        try
        {
            matcher.Track(lease.AgentId, lease.Id.Value.ToString(), value, lease.ExpiresAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    /// <inheritdoc />
    public Task OnRevokedAsync(RevocationScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        switch (scope)
        {
            case AgentRevocation agent:
                matcher.ReleaseAgent(agent.AgentId);
                break;
            case GlobalRevocation:
                matcher.ReleaseAll();
                break;
            default:
                // An unknown scope kind: release everything rather than leave a revoked value matching.
                matcher.ReleaseAll();
                break;
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lease {LeaseId} has no resolvable value; its credential is not tracked by the matcher.")]
    private static partial void LogUnresolved(ILogger logger, Guid leaseId);
}
