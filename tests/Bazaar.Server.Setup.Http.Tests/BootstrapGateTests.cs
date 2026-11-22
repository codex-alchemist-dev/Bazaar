using System.Net;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>The gate before the owner exists: loopback and the one-time bootstrap code, and nothing else.</summary>
public sealed class BootstrapGateTests
{
    [Fact]
    public async Task Bootstrap_code_is_accepted_once_then_refused()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var first = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task A_missing_or_wrong_code_gets_the_same_generic_refusal()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var none = await harness.SendAsync(HttpMethod.Get, "/setup/status");
        using var wrong = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: "not-the-code"));

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await SetupHarness.TextOf(none), await SetupHarness.TextOf(wrong));
        Assert.Equal("{\"error\":\"unauthorized\"}", await SetupHarness.TextOf(wrong));
    }

    [Fact]
    public async Task A_wrong_code_does_not_retire_the_right_one()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var wrong = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: "not-the-code"));
        using var right = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task Non_loopback_caller_is_refused_in_bootstrap_mode_and_the_code_survives()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        // Over https, so the transport rule passes and only the loopback rule can refuse.
        using var remote = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code, Remote: "203.0.113.7", Https: true));
        Assert.Equal(HttpStatusCode.Unauthorized, remote.StatusCode);

        // The refused call did not spend the code, so the loopback caller can still use it.
        using var local = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));
        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
    }

    [Fact]
    public async Task Plaintext_from_off_the_machine_is_refused_before_the_gate()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code, Remote: "203.0.113.7"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"forbidden\"}", await SetupHarness.TextOf(response));
    }

    [Fact]
    public async Task Bootstrap_mode_never_admits_a_bearer_token_for_a_non_owner()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.AgentToken));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
