using Bazaar.Server.Components.Remote;
using Bazaar.Server.Http.Auth;
using Bazaar.Server.SignIn.Passkeys;

namespace Bazaar.Server.Host.Auth;

/// <summary>
/// Binds the passkey enrolment port to the host's access-token verifier: the token must verify, and its device and person must both still
/// be active (the same test as <c>GET /auth/whoami</c>). A missing verifier or lookup answers null, so enrolment refuses.
/// </summary>
public sealed class HostEnrolmentCaller(IServiceProvider services) : IEnrolmentCaller
{
    /// <inheritdoc />
    public async Task<EnrolmentPerson?> ResolveAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        var verifier = services.GetService<IAccessTokenVerifier>();
        var lookup = services.GetService<SignedInPersonLookup>();
        if (verifier is null || lookup is null || !verifier.TryVerify(bearerToken, out var caller))
        {
            return null;
        }

        var person = await lookup.FindAsync(caller.DeviceId, caller.HumanId, cancellationToken).ConfigureAwait(false);
        return person is null ? null : new EnrolmentPerson(person.HumanId, person.DisplayName);
    }
}
