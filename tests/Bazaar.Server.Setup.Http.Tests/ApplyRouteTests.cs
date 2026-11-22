using System.Net;
using System.Text.Json;
using Bazaar.Server.Setup.Http.Tests.Support;

namespace Bazaar.Server.Setup.Http.Tests;

/// <summary>POST /setup/plans/{hash}/apply: the hash check, idempotent replay, the held and failed outcomes, and the status map.</summary>
public sealed class ApplyRouteTests
{
    private static readonly string[] _databaseStep = ["database.schema"];

    private static string ApplyPath(string hash) => "/setup/plans/" + hash + "/apply";

    private static Caller Owner => new(Token: SetupHarness.OwnerToken);

    [Fact]
    public async Task A_wrong_hash_is_refused_with_409_and_runs_nothing()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath(new string('0', 64)), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"error\":\"hash_mismatch\"}", await SetupHarness.TextOf(response));
        Assert.Equal(0, harness.Step.ApplyCalls);
        Assert.Equal(0, harness.Lock.Acquisitions);
        Assert.Empty(harness.RowsOf("setup.plan.applied"));
    }

    [Fact]
    public async Task A_hash_that_is_not_a_plan_hash_is_a_bad_request()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath("not-a-hash"), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Step.ApplyCalls);
    }

    [Fact]
    public async Task An_invalid_plan_on_apply_is_a_bad_request_and_runs_nothing()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var invalid = PlanFixtures.With("\"schemaVersion\": 1", "\"schemaVersion\": 2");

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, invalid);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, harness.Step.ApplyCalls);
        Assert.Equal(0, harness.Lock.Acquisitions);
    }

    [Fact]
    public async Task A_matching_hash_applies_through_the_applier_and_answers_200()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(response));
        Assert.Equal(0, body.GetProperty("exitCode").GetInt32());
        Assert.False(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(_databaseStep, body.GetProperty("appliedSteps").EnumerateArray().Select(s => s.GetString()).ToArray());
        Assert.Equal(1, harness.Step.ApplyCalls);
    }

    [Fact]
    public async Task A_repeat_apply_with_the_same_hash_returns_the_stored_result_without_applying_twice()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var first = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);
        using var second = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(second));
        Assert.True(body.GetProperty("replayed").GetBoolean());
        Assert.Equal(_databaseStep, body.GetProperty("appliedSteps").EnumerateArray().Select(s => s.GetString()).ToArray());
        Assert.Equal(1, harness.Step.ApplyCalls);
        Assert.Equal(1, harness.Results.Count);
        Assert.Single(harness.RowsOf("setup.plan.applied"));
    }

    [Fact]
    public async Task A_failed_apply_rolls_back_answers_500_and_is_replayed_not_re_run()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        harness.Step.Fail = true;
        await harness.StartAsync();

        using var first = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);
        using var second = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
        var body = SetupHarness.Parse(await SetupHarness.TextOf(first));
        Assert.Equal(3, body.GetProperty("exitCode").GetInt32());
        Assert.Empty(body.GetProperty("appliedSteps").EnumerateArray());

        Assert.Equal(HttpStatusCode.InternalServerError, second.StatusCode);
        Assert.Equal(1, harness.Step.ApplyCalls);
    }

    [Fact]
    public async Task A_held_apply_answers_422_changes_nothing_and_is_not_stored()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();
        var held = PlanFixtures.With("\"target\": \"file:/mnt/backup/bazaar\"", "\"target\": null");
        var hash = Bazaar.Server.Setup.SetupPlanLoader.Load(held).PlanHash!;

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath(hash), Owner, held);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, harness.Step.ApplyCalls);
        Assert.Equal(0, harness.Results.Count);
    }

    [Fact]
    public async Task The_apply_row_records_the_api_entry_path_and_the_operator()
    {
        await using var harness = new SetupHarness();
        harness.Owners.Exists = true;
        await harness.StartAsync();

        using var response = await harness.SendAsync(HttpMethod.Post, ApplyPath(PlanFixtures.ValidHash), Owner, PlanFixtures.Valid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var applied = Assert.Single(harness.RowsOf("setup.plan.applied"));
        using var payload = JsonDocument.Parse(applied.Payload);
        Assert.Equal("api", payload.RootElement.GetProperty("entryPath").GetString());
        Assert.Equal(PlanFixtures.ValidHash, payload.RootElement.GetProperty("planHash").GetString());
        Assert.Equal("owner:" + SetupHarness.OwnerId.ToString("D"), payload.RootElement.GetProperty("operator").GetString());
    }

    [Theory]
    [InlineData(SetupExitCode.Ok, 200)]
    [InlineData(SetupExitCode.InvalidInput, 400)]
    [InlineData(SetupExitCode.Held, 422)]
    [InlineData(SetupExitCode.ApplyFailedRolledBack, 500)]
    public void Exit_codes_map_to_one_status_table(SetupExitCode code, int status)
    {
        Assert.Equal(status, SetupHttpStatus.For(code));
    }
}
