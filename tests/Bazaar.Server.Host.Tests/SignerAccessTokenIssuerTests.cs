using System.Security.Cryptography;
using Bazaar.Server.Components.Remote;
using Bazaar.Server.Host.Auth;
using Bazaar.Server.Identity.Signing;

namespace Bazaar.Server.Host.Tests;

/// <summary>The host's issuer bridge fails closed: any signing failure becomes <see cref="AccessTokenIssuerUnavailableException"/> (routes answer 503).</summary>
public sealed class SignerAccessTokenIssuerTests
{
    private sealed class FailingSigner(Exception failure) : IAccessTokenSigner
    {
        public ValueTask<string> SignAsync(Guid deviceId, Guid humanId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<string>(failure);

        public ValueTask<AccessTokenClaims?> VerifyAsync(string token, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AccessTokenClaims?>(null);
    }

    private sealed class ThrowingSynchronouslySigner(Exception failure) : IAccessTokenSigner
    {
        public ValueTask<string> SignAsync(Guid deviceId, Guid humanId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
            throw failure;

        public ValueTask<AccessTokenClaims?> VerifyAsync(string token, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AccessTokenClaims?>(null);
    }

    [Fact]
    public void A_key_that_cannot_be_unwrapped_is_reported_as_unavailable_with_the_cause_inside()
    {
        var cause = new InvalidOperationException("The live access-token signing key could not be unwrapped.");
        var issuer = new SignerAccessTokenIssuer(new FailingSigner(cause));

        var ex = Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public void A_store_that_cannot_be_reached_is_reported_as_unavailable()
    {
        var cause = new System.Net.Sockets.SocketException();
        var issuer = new SignerAccessTokenIssuer(new FailingSigner(cause));

        Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void A_signer_that_throws_before_returning_a_task_is_reported_as_unavailable()
    {
        var cause = new CryptographicException("The KEK is unavailable.");
        var issuer = new SignerAccessTokenIssuer(new ThrowingSynchronouslySigner(cause));

        var ex = Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Same(cause, ex.InnerException);
    }

    [Fact]
    public void An_unavailable_signer_is_passed_through_without_wrapping_it_again()
    {
        var issuer = new SignerAccessTokenIssuer(new UnavailableAccessTokenSigner());

        var ex = Assert.Throws<AccessTokenIssuerUnavailableException>(() => issuer.Issue(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(5)));

        Assert.Null(ex.InnerException);
    }
}
