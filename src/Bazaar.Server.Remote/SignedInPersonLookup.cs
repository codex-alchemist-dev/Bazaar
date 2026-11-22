namespace Bazaar.Server.Components.Remote;

/// <summary>The signed-in person behind a verified access token, as <c>GET /auth/whoami</c> returns it (doc 32 section 5).</summary>
public sealed record SignedInPerson(Guid HumanId, string DisplayName, string Role, Guid DeviceId);

/// <summary>
/// Resolves the human and device named by a verified access token. The token proves a signature and an expiry only; this lookup is what
/// makes revocation and removal immediate (doc 32 section 5). Read-only: it writes nothing and records no audit entry.
/// </summary>
public sealed class SignedInPersonLookup(IRefreshCredentialStore store)
{
    private const string _activeStatus = "active";

    /// <summary>
    /// The person, or null when the device is unknown or revoked, belongs to a different human than the token names, or the human is
    /// unknown, suspended or removed. The caller must not tell the client which of these applied.
    /// </summary>
    public async Task<SignedInPerson?> FindAsync(Guid deviceId, Guid humanId, CancellationToken ct = default)
    {
        var device = await store.FindDeviceAsync(deviceId, ct).ConfigureAwait(false);
        if (device is not { RevokedAt: null } || device.HumanId != humanId) return null;

        var human = await store.FindHumanAsync(humanId, ct).ConfigureAwait(false);
        if (human is null || !string.Equals(human.Status, _activeStatus, StringComparison.Ordinal)) return null;

        return new SignedInPerson(human.Id, human.DisplayName, human.Role, device.Id);
    }
}
