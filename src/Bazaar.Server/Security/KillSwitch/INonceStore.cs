namespace Bazaar.Server.Security.KillSwitch;

/// <summary>A release nonce the server issued and has not yet seen used.</summary>
/// <param name="Nonce">The 128-bit random nonce.</param>
/// <param name="AgentId">The agent the nonce was issued for.</param>
/// <param name="DeviceId">The owner device the nonce was issued to.</param>
/// <param name="ExpiresAt">When the nonce stops being valid.</param>
public sealed record PendingNonce(byte[] Nonce, string AgentId, string DeviceId, DateTimeOffset ExpiresAt);

/// <summary>
/// Pending single-use release nonces. The in-memory implementation is process-local; a durable implementation replaces it
/// behind this interface (a restart must not make a used nonce valid again).
/// </summary>
public interface INonceStore
{
    /// <summary>Records a nonce as pending.</summary>
    Task AddAsync(PendingNonce nonce, CancellationToken ct = default);

    /// <summary>
    /// Atomically removes the nonce and returns what was recorded, or null when it was never issued or was already consumed.
    /// Two concurrent calls for the same nonce return the record to at most one of them. Expiry is the caller's check.
    /// </summary>
    Task<PendingNonce?> TryConsumeAsync(byte[] nonce, CancellationToken ct = default);
}
