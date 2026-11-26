using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.SignIn.Postgres;

/// <summary>
/// The panel's <see cref="IAdminHandoffRedeemer"/> over <see cref="IHandoffCodeStore"/>, for a process that holds the shared store but
/// not the sign-in module (the bazaar-tui host). Redeems for the admin audience only, like the sign-in module's own adapter.
/// </summary>
public sealed class PostgresAdminHandoffRedeemer(IHandoffCodeStore store, TimeProvider time) : IAdminHandoffRedeemer
{
    public async Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default)
    {
        var identity = await store.RedeemAsync(code, SignInAudiences.Admin, time.GetUtcNow(), ct).ConfigureAwait(false);
        return identity is null ? null : new AdminIdentity(identity.PrincipalId, identity.Kind, identity.DeviceId);
    }
}
