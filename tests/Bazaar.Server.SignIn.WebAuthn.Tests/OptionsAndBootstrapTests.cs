using Bazaar.Server.SignIn.WebAuthn.Registration;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.SignIn.WebAuthn.Tests;

public sealed class OptionsAndBootstrapTests
{
    private static WebAuthnOptions Read(Dictionary<string, string?> values) =>
        WebAuthnOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void Nothing_is_configured_by_default()
    {
        var options = Read([]);

        Assert.False(options.IsConfigured);
        Assert.Null(options.RpId);
        Assert.Empty(options.AllowedOrigins);
        Assert.Null(options.BootstrapPrincipalId);
        Assert.True(options.BootstrapEnabled);
    }

    [Fact]
    public void Origins_come_from_operator_settings_as_a_list_or_comma_separated_value()
    {
        var listed = Read(new() { ["signin:rpId"] = "bazaar.example", ["signin:webauthn:origins:0"] = "https://bazaar.example", ["signin:webauthn:origins:1"] = "https://app.bazaar.example" });
        var csv = Read(new() { ["signin:rpId"] = "Bazaar.Example", ["signin:webauthn:origins"] = "https://bazaar.example, https://bazaar.example" });

        Assert.Equal(["https://bazaar.example", "https://app.bazaar.example"], listed.AllowedOrigins);
        Assert.Equal("bazaar.example", csv.RpId);
        Assert.Equal(["https://bazaar.example"], csv.AllowedOrigins);
        Assert.True(listed.IsConfigured);
    }

    [Theory]
    [InlineData("http://bazaar.example")]
    [InlineData("https://bazaar.example/")]
    [InlineData("https://bazaar.example/path")]
    [InlineData("https://other.example")]
    [InlineData("https://evilbazaar.example")]
    [InlineData("https://user@bazaar.example")]
    [InlineData("not a url")]
    public void Origins_that_are_not_https_origins_under_the_rp_id_are_dropped(string origin)
    {
        var options = Read(new() { ["signin:rpId"] = "bazaar.example", ["signin:webauthn:origins"] = origin });

        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void Plain_http_is_allowed_for_localhost_only()
    {
        Assert.True(Read(new() { ["signin:rpId"] = "localhost", ["signin:webauthn:origins"] = "http://localhost:5000" }).IsConfigured);
    }

    [Theory]
    [InlineData("https://bazaar.example")]
    [InlineData("bazaar.example:443")]
    [InlineData("a b")]
    public void A_malformed_rp_id_leaves_the_module_unconfigured(string rpId)
    {
        Assert.False(Read(new() { ["signin:rpId"] = rpId, ["signin:webauthn:origins"] = "https://bazaar.example" }).IsConfigured);
    }

    [Fact]
    public void Bootstrap_settings_are_read()
    {
        var id = Guid.NewGuid();
        var options = Read(new() { ["signin:webauthn:bootstrapPrincipalId"] = id.ToString(), ["signin:webauthn:bootstrapEnabled"] = "false" });

        Assert.Equal(id, options.BootstrapPrincipalId);
        Assert.False(options.BootstrapEnabled);
        Assert.Null(Read(new() { ["signin:webauthn:bootstrapPrincipalId"] = Guid.Empty.ToString() }).BootstrapPrincipalId);
    }

    [Fact]
    public void Bootstrap_code_is_single_use_and_bound_to_one_principal()
    {
        var svc = new BootstrapCodeService();
        var principal = Guid.NewGuid();
        var code = svc.Issue(principal);

        Assert.False(svc.Verify(code, Guid.NewGuid()));
        Assert.True(svc.Verify(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), principal));
        Assert.True(svc.TryConsume(principal));
        Assert.False(svc.TryConsume(principal));
        Assert.False(svc.Verify(code, principal));
    }

    [Fact]
    public void Bootstrap_code_burns_after_five_wrong_attempts()
    {
        var svc = new BootstrapCodeService();
        var principal = Guid.NewGuid();
        var code = svc.Issue(principal);

        for (var i = 0; i < BootstrapCodeService.MaxFailures; i++)
        {
            Assert.False(svc.Verify("wrong", principal));
        }

        Assert.False(svc.Verify(code, principal));
        Assert.False(svc.IsActive(principal));
    }

    [Fact]
    public void Registration_challenges_are_single_use_and_expire()
    {
        var store = new RegistrationChallengeStore();
        var now = DateTimeOffset.UnixEpoch;
        var principal = Guid.NewGuid();

        var c = store.Issue(principal, "k", viaBootstrap: false, now);
        var got = store.Consume(c.Id, now);

        Assert.Equal(principal, got!.PrincipalId);
        Assert.Null(store.Consume(c.Id, now));
        Assert.Null(store.Consume(store.Issue(principal, "k", false, now).Id, now + RegistrationChallengeStore.Lifetime));
    }
}
