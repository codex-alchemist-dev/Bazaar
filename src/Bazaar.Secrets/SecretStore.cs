using System.Security.Cryptography;

namespace Bazaar.Secrets;

/// <summary>
/// Host-side secret store (doc 14, <see cref="ISecretStore"/>). Each scope has one data key, wrapped by the KEK.
/// Each secret version is sealed under that data key with its scope, name and version bound as associated data,
/// so a record copied to another scope fails to open. Agent callers are always denied. Every operation writes its
/// audit event first, and a failed audit write stops the operation.
/// </summary>
public sealed class SecretStore(
    IKekProvider kek,
    ISecretRecordRepository records,
    IAgentRegistry agents,
    ISecretAuditSink audit,
    TimeProvider time) : ISecretStore
{
    private const int _dataKeyBytes = 32;

    public async Task PutAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretValue value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        await AuthorizeAsync(caller, scope, SecretAuditAction.Put, name, null, requireActive: true, ct);

        var dataKey = await DataKeyAsync(scope, create: true, ct);
        try
        {
            var current = await records.GetSecretAsync(scope.Key, name.Value, ct);
            var version = (current?.Version ?? 0) + 1;
            var sealedValue = SecretCrypto.Seal(dataKey, value.Bytes, SecretCrypto.SecretAad(scope.Key, name.Value, version));
            await records.PutSecretAsync(
                new SecretRecord(scope.Key, name.Value, version, sealedValue.Nonce, sealedValue.Ciphertext, sealedValue.Tag, time.GetUtcNow()),
                ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task<SecretValue> GetAsync(SecretCaller caller, SecretScope scope, SecretName name, SecretPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(purpose);
        await AuthorizeAsync(caller, scope, SecretAuditAction.Get, name, purpose, requireActive: true, ct);

        var stored = await records.GetSecretAsync(scope.Key, name.Value, ct)
            ?? throw new SecretNotFoundException($"No secret '{name.Value}' in scope '{scope.Key}'.");
        var dataKey = await DataKeyAsync(scope, create: false, ct);
        try
        {
            var aad = SecretCrypto.SecretAad(scope.Key, name.Value, stored.Version);
            var plaintext = SecretCrypto.Open(dataKey, stored.Nonce.Span, stored.Ciphertext.Span, stored.Tag.Span, aad);
            try
            {
                return new SecretValue(plaintext);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task DeleteAsync(SecretCaller caller, SecretScope scope, SecretName name, CancellationToken ct)
    {
        await AuthorizeAsync(caller, scope, SecretAuditAction.Delete, name, null, requireActive: true, ct);
        if (!await records.DeleteSecretAsync(scope.Key, name.Value, ct))
        {
            throw new SecretNotFoundException($"No secret '{name.Value}' in scope '{scope.Key}'.");
        }
    }

    public async Task CryptoEraseAsync(SecretCaller caller, AgentId agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var scope = SecretScope.ForAgent(agent);
        await AuthorizeAsync(caller, scope, SecretAuditAction.CryptoErase, null, null, requireActive: false, ct);

        // Destroy the data key first, so the ciphertext is unreadable even if the row delete is interrupted.
        await records.EraseScopeKeyAsync(scope.Key, time.GetUtcNow(), ct);
        await records.DeleteScopeSecretsAsync(scope.Key, ct);
    }

    /// <summary>
    /// Gate for every operation. Writes one audit event (allowed or denied) before returning or throwing.
    /// Agent callers are denied outright (doc 14). Host callers pass only when an agent-owned scope is Active
    /// (or, when <paramref name="requireActive"/> is false, registered).
    /// </summary>
    private async Task AuthorizeAsync(
        SecretCaller caller,
        SecretScope scope,
        SecretAuditAction action,
        SecretName? name,
        SecretPurpose? purpose,
        bool requireActive,
        CancellationToken ct)
    {
        string label;
        string? denial = null;
        switch (caller)
        {
            case HostCaller host:
                label = "host:" + host.Component;
                break;
            case AgentCaller agentCaller:
                label = "agent:" + agentCaller.Agent.Value;
                denial = "agents cannot reach the secret store";
                break;
            default:
                label = "unknown";
                denial = "unknown caller";
                break;
        }

        if (denial is null && scope.Agent is { } owner)
        {
            var registered = await agents.GetAsync(owner, ct);
            if (registered is null)
            {
                denial = "agent is not registered";
            }
            else if (requireActive && registered.State != AgentState.Active)
            {
                denial = $"agent is {registered.State}";
            }
        }

        var outcome = denial is null ? SecretAuditOutcome.Allowed : SecretAuditOutcome.Denied;
        await audit.RecordAsync(
            new SecretAuditEvent(time.GetUtcNow(), action, outcome, label, scope.Key, name?.Value, purpose?.Value, denial),
            ct);

        if (denial is not null)
        {
            throw new SecretAccessDeniedException(denial);
        }
    }

    /// <summary>Returns the plaintext data key for a scope. Creates it when <paramref name="create"/> is true. Caller zeroes it.</summary>
    private async Task<byte[]> DataKeyAsync(SecretScope scope, bool create, CancellationToken ct)
    {
        var existing = await records.GetScopeKeyAsync(scope.Key, ct);
        if (existing is null)
        {
            if (!create)
            {
                throw new SecretNotFoundException($"Scope '{scope.Key}' has no data key.");
            }

            var fresh = RandomNumberGenerator.GetBytes(_dataKeyBytes);
            var wrapped = await kek.WrapAsync(fresh, SecretCrypto.DataKeyAad(scope.Key, kek.CurrentKeyId), ct);
            var record = new ScopeKeyRecord(scope.Key, kek.CurrentKeyId, wrapped, time.GetUtcNow(), null);
            if (await records.TryInsertScopeKeyAsync(record, ct))
            {
                return fresh;
            }

            // Another writer created the key first. Discard ours and use theirs.
            CryptographicOperations.ZeroMemory(fresh);
            existing = await records.GetScopeKeyAsync(scope.Key, ct)
                ?? throw new InvalidOperationException("The scope's data key disappeared after a lost insert race.");
        }

        if (existing.DestroyedAt is not null)
        {
            throw new SecretAccessDeniedException("this scope's data key was crypto-erased");
        }

        var key = await kek.UnwrapAsync(existing.KeyId, existing.WrappedKey, SecretCrypto.DataKeyAad(scope.Key, existing.KeyId), ct);
        return key.ToArray();
    }
}
