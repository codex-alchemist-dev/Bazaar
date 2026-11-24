
namespace Bazaar.Server.WebAdmin.Security;

/// <summary>Default revocation check when the identity module is not wired: nothing is ever revoked. Sessions are short, so this is acceptable.</summary>
public sealed class NoRevocation : IAdminRevocationCheck
{
    public Task<bool> IsRevokedAsync(Guid principalId, Guid deviceId, CancellationToken ct = default) => Task.FromResult(false);
}
