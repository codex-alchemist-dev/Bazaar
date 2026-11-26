using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Contracts.Engines;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.EngineDispatch;
using Bazaar.Server.Security.Audit;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.EngineTool.Tests;

public sealed class EngineCallToolTests
{
    private static readonly Guid _agent = Guid.NewGuid();
    private const string _device = "dev-1";

    private sealed class Grants(bool covers) : IEngineGrantReader
    {
        public ValueTask<bool> CoversAsync(Guid agentId, string adapterId, string toolName, RiskClass riskClass, string grantId, CancellationToken ct) =>
            ValueTask.FromResult(covers);
    }

    private sealed class Taint : ISessionTaintReader
    {
        public ValueTask<bool> IsHardTaintedAsync(Guid agentId, Guid? sessionId, CancellationToken ct) => ValueTask.FromResult(false);
    }

    private sealed class Recorder : IEngineGcRecorder
    {
        public List<EngineToolCallRecord> Records { get; } = [];

        public Task RecordAsync(EngineToolCallRecord record, CancellationToken ct)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private sealed class Audit : IAuditLog
    {
        public Task AppendAsync(AuditEntry e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Targets(string? device) : IRelayTargetDirectory
    {
        public ValueTask<string?> FindTargetDeviceAsync(CancellationToken ct) => ValueTask.FromResult(device);
    }

    private sealed class Publisher : IRelayPublisher
    {
        public ConcurrentQueue<EngineRelayRequestEvent> Sent { get; } = new();

        public ValueTask PublishAsync(string deviceId, EngineRelayRequestEvent relayEvent, CancellationToken ct)
        {
            Sent.Enqueue(relayEvent);
            return ValueTask.CompletedTask;
        }
    }

    private static (EngineCallTool Tool, EngineRelayBroker Broker, Recorder Recorder, Publisher Publisher) Rig(bool covers = true, string? device = _device)
    {
        var recorder = new Recorder();
        var publisher = new Publisher();
        var broker = new EngineRelayBroker(
            new EngineToolDispatcher(new Grants(covers), new Taint(), recorder, new Audit(), new EngineDispatchOptions { RobloxExecOwnerApproval = false }),
            new Targets(device),
            publisher,
            new EngineRelayOptions { Timeout = TimeSpan.FromSeconds(30) });
        return (new EngineCallTool(broker), broker, recorder, publisher);
    }

    private static JsonElement Json(string json) => JsonElement.Parse(json);

    private static ToolCallContext Context(Guid? gc = null) => new(_agent, Guid.NewGuid(), Guid.NewGuid(), 0, gc);

    private static string Status(string result) => JsonElement.Parse(result).GetProperty("status").GetString()!;

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"adapter_id":"roblox","studio_id":"s"}""")]
    [InlineData("""{"adapter_id":"roblox","studio_id":"s","tool":"multi_edit","args":[1]}""")]
    [InlineData("""{"adapter_id":"roblox","studio_id":"s","tool":"multi_edit","grant_id":""}""")]
    public async Task Bad_arguments_are_refused_without_a_relay(string args)
    {
        var rig = Rig();

        var result = await rig.Tool.ExecuteAsync(Json(args), Context(), TestContext.Current.CancellationToken);

        Assert.Equal("bad_arguments", Status(result));
        Assert.Empty(rig.Publisher.Sent);
        Assert.Equal(0, rig.Broker.PendingCount);
    }

    [Fact]
    public async Task The_caller_and_the_working_gc_come_from_the_context_not_the_arguments()
    {
        var ct = TestContext.Current.CancellationToken;
        var rig = Rig();
        var gc = Guid.NewGuid();

        var call = rig.Tool.ExecuteAsync(Json("""{"adapter_id":"roblox","studio_id":"s","tool":"multi_edit","grant_id":"g1","agent_id":"ignored","args":{"x":1}}"""), Context(gc), ct);
        EngineRelayRequestEvent evt;
        while (!rig.Publisher.Sent.TryDequeue(out evt!))
            await Task.Delay(5, ct);
        rig.Broker.SubmitResult(_device, evt.RelayId, new EngineRelayCallResult("g1", true, Json("""{"done":true}""")));
        var result = await call;

        var record = Assert.Single(rig.Recorder.Records);
        Assert.Equal(_agent, record.AgentId);
        Assert.Equal(gc, record.GcId);
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("completed", doc.RootElement.GetProperty("status").GetString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("done").GetBoolean());
    }

    [Fact]
    public async Task A_grant_that_does_not_cover_is_denied_with_its_reason()
    {
        var rig = Rig(covers: false);

        var result = await rig.Tool.ExecuteAsync(Json("""{"adapter_id":"roblox","studio_id":"s","tool":"multi_edit","grant_id":"g1"}"""), Context(Guid.NewGuid()), TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("denied", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("GrantNotCovering", doc.RootElement.GetProperty("reason").GetString());
        Assert.Empty(rig.Publisher.Sent);
    }

    [Fact]
    public async Task No_connected_desktop_reports_unavailable()
    {
        var rig = Rig(device: null);

        var result = await rig.Tool.ExecuteAsync(Json("""{"adapter_id":"roblox","studio_id":"s","tool":"multi_edit","grant_id":"g1"}"""), Context(Guid.NewGuid()), TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", Status(result));
    }

    [Fact]
    public void An_oversized_answer_is_reported_by_size()
    {
        var big = new string('x', EngineCallTool.MaxResultBytes + 1);
        var outcome = EngineRelayOutcome.Completed(new EngineRelayCallResult("g1", true, Json("{\"t\":\"" + big + "\"}")));

        var result = EngineCallTool.Shape(outcome);

        using var doc = JsonDocument.Parse(result);
        Assert.Equal("too_large", doc.RootElement.GetProperty("status").GetString());
        Assert.False(doc.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public void The_component_registers_the_tool_policy_and_definition()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        new EngineToolComponent().AddServices(services, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(ToolCapability));
        Assert.Contains(services, d => d.ServiceType == typeof(ToolRegistration));
        Assert.Equal("engine.call", EngineToolComponent.Definition.Name);
        Assert.Contains("engine-relay", EngineToolComponent.ManifestDefinition.DependsOn);
    }
}
