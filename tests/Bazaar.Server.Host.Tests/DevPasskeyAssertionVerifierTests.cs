using Bazaar.Roles.Abstractions;
using Bazaar.Server.Host.Composition;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Host.Tests;

public class DevPasskeyAssertionVerifierTests
{
    private static DevPasskeyAssertionVerifier Verifier(string principal) =>
        new(Options.Create(new DevAuthOptions { Enabled = true, PrincipalId = principal }), () => "secret-token");

    [Fact]
    public async Task The_dev_token_signs_in_as_the_dev_principal_on_a_fresh_device()
    {
        var id = Guid.NewGuid();
        var verifier = Verifier(id.ToString("D"));

        var first = await verifier.VerifyAsync("challenge", "secret-token", TestContext.Current.CancellationToken);
        var second = await verifier.VerifyAsync("challenge", "secret-token", TestContext.Current.CancellationToken);

        Assert.NotNull(first);
        Assert.Equal(id, first.PrincipalId);
        Assert.Equal(PrincipalKind.Human, first.Kind);
        Assert.NotEqual(first.DeviceId, second!.DeviceId);
    }

    [Theory]
    [InlineData("challenge", "other")]
    [InlineData("challenge", "")]
    [InlineData("", "secret-token")]
    public async Task Anything_else_fails(string challenge, string assertion)
    {
        var result = await Verifier("owner").VerifyAsync(challenge, assertion, TestContext.Current.CancellationToken);
        Assert.Null(result);
    }
}
