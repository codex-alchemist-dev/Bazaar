using System.Collections.Concurrent;
using Bazaar.Server.Identity;

namespace Bazaar.Server.Identity.Leases;

/// <summary>
/// Answers whether an agent has a Passport entry for a service (doc 14, "Access flow" step 1). Only the existence
/// and id of the credential are exposed here, never its secret.
/// </summary>
public interface ICredentialDirectory
{
    /// <summary>Returns the credential that <paramref name="agentId"/> owns for <paramref name="service"/>, or null when none exists.</summary>
    Task<CredentialId?> FindCredentialAsync(string agentId, string service, CancellationToken ct = default);
}

/// <summary>In-memory <see cref="ICredentialDirectory"/>, for tests and fakes.</summary>
public sealed class InMemoryCredentialDirectory : ICredentialDirectory
{
    private readonly ConcurrentDictionary<(string AgentId, string Service), CredentialId> _entries = new();

    /// <summary>Registers that <paramref name="agentId"/> owns <paramref name="credentialId"/> for <paramref name="service"/>.</summary>
    public void Add(string agentId, string service, CredentialId credentialId) =>
        _entries[(agentId, service)] = credentialId;

    /// <inheritdoc />
    public Task<CredentialId?> FindCredentialAsync(string agentId, string service, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<CredentialId?>(_entries.TryGetValue((agentId, service), out var id) ? id : null);
    }
}
