using System.Net;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>GET /setup/status: step progress, held approvals, the optional verification, and 503 when a dependency is missing.</summary>
public sealed class StatusRouteTests
{
    [Fact]
    public async Task Status_reports_steps_and_held_approvals_and_a_null_verification_when_none_is_registered()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(response));
        var step = Assert.Single(body.GetProperty("steps").EnumerateArray());
        Assert.Equal("database.schema", step.GetProperty("id").GetString());
        Assert.Equal("done", step.GetProperty("state").GetString());
        var held = Assert.Single(body.GetProperty("heldApprovals").EnumerateArray());
        Assert.Equal("install.updatePolicy", held.GetProperty("path").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("lastVerification").ValueKind);
    }

    [Fact]
    public async Task Status_reports_the_last_verification_when_a_source_is_registered()
    {
        await using var harness = new SetupHarness
        {
            Verification = new Bazaar.Server.Setup.Http.SetupVerificationSummary(
                Bazaar.Server.Setup.Http.SetupVerdict.Amber, 72, new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero)),
        };
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Token: SetupHarness.OwnerToken));

        var body = SetupHarness.Parse(await SetupHarness.TextOf(response));
        var verification = body.GetProperty("lastVerification");
        Assert.Equal("amber", verification.GetProperty("verdict").GetString());
        Assert.Equal(72, verification.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task A_missing_gate_answers_503_and_writes_no_audit_row()
    {
        await using var harness = new SetupHarness { RegisterGate = false };
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Get, "/setup/status", new Caller(Code: "anything"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"unavailable\"}", await SetupHarness.TextOf(response));
        Assert.Empty(harness.Audit.Entries);
    }
}
