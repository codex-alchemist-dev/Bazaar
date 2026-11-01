using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Runner;
using Bazaar.Server.Gateway.Fakes;
using Npgsql;

namespace Bazaar.Server.Tests.Agents.Turns.Runner;

/// <summary>Runner pieces that need no database: options, request hashes, payload JSON and the cancelled-before-start path.</summary>
public sealed class TurnRunnerUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Defaults_are_the_shipped_values()
    {
        var options = new TurnRunnerOptions();

        Assert.Equal(40, options.StepCap);
        Assert.Equal(200_000L, options.TokenCeiling);
        Assert.Equal(TimeSpan.FromMinutes(10), options.WallClock);
        Assert.Equal(TimeSpan.FromMinutes(2), options.LeaseDuration);
        Assert.Empty(options.IdempotentTools);
        Assert.Empty(options.ToolCatalogue);
        options.Validate();
    }

    [Fact]
    public void Validate_rejects_a_zero_step_cap_and_a_zero_token_ceiling()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnRunnerOptions { StepCap = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnRunnerOptions { TokenCeiling = 0 }.Validate());
    }

    [Fact]
    public void Validate_rejects_a_zero_wall_clock_and_a_zero_lease()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnRunnerOptions { WallClock = TimeSpan.Zero }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TurnRunnerOptions { LeaseDuration = TimeSpan.Zero }.Validate());
    }

    [Fact]
    public void Tool_hash_is_stable_and_depends_on_the_model_response()
    {
        var call = new ModelToolCall("c1", "lookup", "{}");

        Assert.Equal(TurnStepHash.ToolCall(1, call), TurnStepHash.ToolCall(1, call));
        // The same call id and arguments in a later model response is a different step.
        Assert.NotEqual(TurnStepHash.ToolCall(1, call), TurnStepHash.ToolCall(2, call));
        Assert.NotEqual(TurnStepHash.ToolCall(1, call), TurnStepHash.ToolCall(1, call with { ArgumentsJson = "{\"a\":1}" }));
    }

    [Fact]
    public void Hash_parts_cannot_run_together()
    {
        Assert.NotEqual(TurnStepHash.Of("ab", "c"), TurnStepHash.Of("a", "bc"));
    }

    [Fact]
    public void Completed_step_payloads_round_trip_through_the_json_context()
    {
        var model = new ModelStepPayload(
            "Looking.",
            [new ModelToolCall("c1", "lookup", "{\"q\":1}", "sig")],
            new GatewayTokenUsage(10, 1, 2, 3, 4, 5),
            StopReason.ToolUse,
            "served-model");
        var tool = new ToolStepPayload("c1", ToolDispatchOutcome.Denied, null, "policy");
        var post = new PostStepPayload("gc-1");

        var modelJson = JsonSerializer.Serialize(model, TurnRunnerJson.Default.ModelStepPayload);
        var toolJson = JsonSerializer.Serialize(tool, TurnRunnerJson.Default.ToolStepPayload);
        var postJson = JsonSerializer.Serialize(post, TurnRunnerJson.Default.PostStepPayload);

        Assert.Equal(model.Text, JsonSerializer.Deserialize(modelJson, TurnRunnerJson.Default.ModelStepPayload)!.Text);
        Assert.Equal(model.ToolCalls, JsonSerializer.Deserialize(modelJson, TurnRunnerJson.Default.ModelStepPayload)!.ToolCalls);
        Assert.Equal(model.Usage, JsonSerializer.Deserialize(modelJson, TurnRunnerJson.Default.ModelStepPayload)!.Usage);
        Assert.Equal(tool, JsonSerializer.Deserialize(toolJson, TurnRunnerJson.Default.ToolStepPayload));
        Assert.Equal(post, JsonSerializer.Deserialize(postJson, TurnRunnerJson.Default.PostStepPayload));
    }

    [Fact]
    public async Task A_run_cancelled_before_it_starts_is_interrupted_without_touching_the_database()
    {
        // The data source is never opened: a cancelled token returns before any repository call.
        using var dataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
        var runner = new TurnRunner(
            new LeaseRepository(dataSource),
            new InboxRepository(dataSource),
            new TurnRepository(dataSource),
            new TurnStepRepository(dataSource),
            new RunnerGateway(new FakeProvider()),
            new FakeContextAssembler(),
            new FakeToolDispatcher(),
            new FakePostSink(),
            TimeProvider.System,
            new TurnRunnerOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var request = new TurnRunRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), TurnRole.Actor, "mention", [], "turn-a", "route", []);

        var outcome = await runner.RunAsync(request, cts.Token);

        Assert.Equal(TurnEndReason.Interrupted, outcome.Reason);
        Assert.Equal(Guid.Empty, outcome.TurnId);
        Assert.Equal(0, outcome.StepCount);
    }
}
