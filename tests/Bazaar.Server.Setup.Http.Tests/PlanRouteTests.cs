using System.Net;
using System.Text.Json;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>POST /setup/plans: validation and diagnostics. It writes nothing, and its issues carry paths and rules, never values.</summary>
public sealed class PlanRouteTests
{
    private const string _json = "application/json";

    [Fact]
    public async Task A_valid_plan_returns_its_hash_and_writes_nothing()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, "/setup/plans", new Caller(Token: SetupHarness.OwnerToken), PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(response));
        Assert.Equal(0, body.GetProperty("exitCode").GetInt32());
        Assert.Equal(PlanFixtures.ValidHash, body.GetProperty("planHash").GetString());
        Assert.Empty(body.GetProperty("issues").EnumerateArray());

        Assert.Equal(0, harness.Step.ApplyCalls);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Equal(0, harness.Results.Count);
        Assert.Empty(harness.RowsOf("setup.plan.applied"));
    }

    [Fact]
    public async Task An_invalid_plan_returns_diagnostics_with_paths_and_no_values_and_writes_nothing()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var invalid = PlanFixtures.With("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        using var response = await harness.SendAsync(HttpMethod.Post, "/setup/plans", new Caller(Token: SetupHarness.OwnerToken), invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await SetupHarness.TextOf(response);
        var body = SetupHarness.Parse(text);
        Assert.Equal(1, body.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("planHash").ValueKind);
        var issue = Assert.Single(body.GetProperty("issues").EnumerateArray(), i => i.GetProperty("path").GetString() == "schemaVersion");
        Assert.Equal("error", issue.GetProperty("severity").GetString());

        // No value the operator typed is echoed back.
        Assert.DoesNotContain("Example Studio", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example-studio", text, StringComparison.Ordinal);
        Assert.Equal(0, harness.Step.ApplyCalls);
        Assert.Equal(0, harness.Results.Count);
    }

    [Fact]
    public async Task A_plan_with_only_a_hold_answers_422_with_its_hash()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var held = PlanFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null");

        using var response = await harness.SendAsync(HttpMethod.Post, "/setup/plans", new Caller(Token: SetupHarness.OwnerToken), held);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(response));
        Assert.Equal(2, body.GetProperty("exitCode").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("planHash").ValueKind);
        Assert.Equal(0, harness.Step.ApplyCalls);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused_as_media_type()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(
            HttpMethod.Post, "/setup/plans", new Caller(Token: SetupHarness.OwnerToken), PlanFixtures.Valid, "text/plain");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_parsed()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var huge = "{" + new string(' ', SetupHttpConstants.MaxPlanBytes + 1) + "}";

        using var response = await harness.SendAsync(HttpMethod.Post, "/setup/plans", new Caller(Token: SetupHarness.OwnerToken), huge, _json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"too_large\"}", await SetupHarness.TextOf(response));
    }

    [Fact]
    public async Task Validation_is_refused_for_a_caller_outside_the_gate_and_nothing_is_parsed()
    {
        await using var harness = new SetupHarness();
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, "/setup/plans", null, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, harness.Lock.Acquisitions);
    }
}
