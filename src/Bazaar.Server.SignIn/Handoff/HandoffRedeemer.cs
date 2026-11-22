using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.SignIn.Handoff;

/// <summary>Adapts <see cref="IHandoffCodeStore"/> to the panel's <see cref="IAdminHandoffRedeemer"/> port. Redeems for the admin audience only.</summary>
public sealed class HandoffRedeemer(IHandoffCodeStore store, TimeProvider time) : IAdminHandoffRedeemer
{
    public async Task<AdminIdentity?> RedeemAsync(string code, CancellationToken ct = default)
    {
        var identity = await store.RedeemAsync(code, SignInAudiences.Admin, time.GetUtcNow(), ct);
        return identity is null ? null : new AdminIdentity(identity.PrincipalId, identity.Kind, identity.DeviceId);
    }
}

/// <summary>Fail-closed default until a passkey adapter is registered: no assertion is ever valid.</summary>
public sealed class DenyAllPasskeyVerifier : IPasskeyAssertionVerifier
{
    public Task<SignInIdentity?> VerifyAsync(string challenge, string assertionJson, CancellationToken ct = default) =>
        Task.FromResult<SignInIdentity?>(null);
}
