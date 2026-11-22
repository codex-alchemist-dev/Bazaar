using System.Net;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>The gate after the owner exists: only the owner role passes, and the bootstrap code is never consulted again.</summary>
public sealed class OwnerGateTests
{
    [Fact]
    public async Task After_the_owner_exists_the_bootstrap_code_is_ignored()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var code = await harness.Codes.CreateAsync(TestContext.Current.CancellationToken);

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: code));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("{\"error\":\"unauthorized\"}", await SetupHarness.TextOf(response));
    }

    [Fact]
    public async Task The_owner_role_passes()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_valid_credential_without_the_owner_role_is_forbidden()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.AgentToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"forbidden\"}", await SetupHarness.TextOf(response));
    }

    [Fact]
    public async Task An_unknown_bearer_token_is_unauthorized()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: "made-up-token"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Owner_mode_refuses_plaintext_from_off_the_machine_even_with_the_owner_token()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken, Remote: "203.0.113.7"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_owner_can_call_the_apply_route_and_the_applier_records_the_owner_as_operator()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post,
            "/setup/plans/" + PlanFixtures.ValidHash + "/apply",
            new Caller(Token: SetupHarness.OwnerToken),
            PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = Assert.Single(harness.RowsOf("setup.plan.applied"));
        Assert.Equal("owner:" + SetupHarness.OwnerId.ToString("D"), applied.Actor);
    }
}
