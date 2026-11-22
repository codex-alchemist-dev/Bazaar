using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingRunner;
using Bazaar.Server.CodingRunner.Handlers;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Security.Policy;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.DependencyInjection;
using ModuleTaint = Bazaar.Server.CodingPermissions.SessionTaint;
using ServerTaint = Bazaar.Server.Security.Taint.SessionTaint;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class CodingToolDispatchTests
{
    private static readonly string[] _expectedTools =
        ["edit", "search", "repo_map", "git_diff", "git_commit", "git_push_draft_pr", "review", "subagent_spawn"];

    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public void Offers_cover_the_eight_tools_once_each()
    {
        Assert.Equal(_expectedTools, CodingToolDispatchExtensions.Offers.Select(o => o.Name));
        Assert.All(CodingToolDispatchExtensions.Offers, o => Assert.Equal(o.Name, o.Capability.Name));
    }

    [Fact]
    public void Every_schema_is_a_closed_json_object()
    {
        foreach (var offer in CodingToolDispatchExtensions.Offers)
        {
            using var doc = JsonDocument.Parse(offer.ParametersJsonSchema);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            // The handlers reject an argument they do not list, so a schema that allowed extras would invite refused calls.
            Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean(), offer.Name);
        }
    }

    [Fact]
    public void Only_edit_and_reads_are_autonomous_and_every_tool_is_actor_only()
    {
        foreach (var offer in CodingToolDispatchExtensions.Offers)
        {
            Assert.Equal(SessionRoles.Actor, offer.Capability.Roles);
            var autonomous = offer.Capability.Tier == ToolTier.Autonomous;
            Assert.Equal(offer.Name is "edit" or "search" or "repo_map" or "git_diff", autonomous);
        }

        Assert.Equal(CapabilityClass.Push, CodingToolDispatchExtensions.Offers.Single(o => o.Name == "git_push_draft_pr").Capability.Class);
    }

    [Fact]
    public void Dispatch_registers_a_tool_a_capability_and_a_definition_per_tool_and_the_registry_accepts_them()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICodingToolRunner>(new CapturingRunner());
        services.AddSingleton<ISessionTaintStore>(new FixedTaint(ServerTaint.Clean));
        services.AddBazaarCodingToolDispatch();

        // Only the descriptors are checked: the handlers' wrapped services are the host's to bind.
        Assert.Equal(8, services.Count(d => d.ServiceType == typeof(ToolRegistration)));
        Assert.Equal(8, services.Count(d => d.ServiceType == typeof(ToolCapability)));
        Assert.Equal(8, services.Count(d => d.ServiceType == typeof(ModelToolDefinition)));

        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetServices<ToolRegistration>().ToList();
        var registry = new ToolRegistry(registrations.Select(r => (r.Name, r.Tool)));
        Assert.All(_expectedTools, name => Assert.True(registry.TryGet(name, out _)));
        Assert.All(registrations, r => Assert.IsAssignableFrom<IContextualTool>(r.Tool));
    }

    [Fact]
    public async Task The_bridge_takes_agent_session_taint_and_worktree_from_the_host_not_the_arguments()
    {
        var runner = new CapturingRunner();
        var agent = Guid.NewGuid();
        var session = Guid.NewGuid();
        var keys = new CodingSessionWorktreeKeys();
        keys.Set(session, "issued-key");
        var bridge = new CodingToolBridge("git_push_draft_pr", runner, new FixedTaint(new ServerTaint(TaintState.Hard, [])), keys);
        using var args = JsonDocument.Parse("""{"title":"t","branch":"agent/x","worktree_key":"someone-elses"}""");

        await bridge.ExecuteAsync(args.RootElement, new Bazaar.Server.Agents.Turns.Tools.ToolCallContext(agent, session, Guid.NewGuid()), _ct);

        var call = Assert.Single(runner.Calls);
        Assert.Equal(agent.ToString("D"), call.AgentId);
        Assert.Equal(session.ToString("D"), call.SessionId);
        Assert.Equal("issued-key", call.WorktreeKey);
        Assert.Equal(ModuleTaint.Hard, call.Taint);
        Assert.Equal("external", call.SourceTrust);
        Assert.Equal(ToolSessionRole.Actor, call.Role);
        Assert.Equal("agent/x", call.TargetBranch);
        Assert.Null(call.Grant);
    }

    [Theory]
    [InlineData(TaintState.Clean, ModuleTaint.None, "agent")]
    [InlineData(TaintState.Soft, ModuleTaint.Soft, "agent")]
    [InlineData(TaintState.Hard, ModuleTaint.Hard, "external")]
    public async Task The_bridge_maps_each_taint_level(TaintState state, ModuleTaint expected, string trust)
    {
        var runner = new CapturingRunner();
        var bridge = new CodingToolBridge("search", runner, new FixedTaint(new ServerTaint(state, [])), new CodingSessionWorktreeKeys());
        using var args = JsonDocument.Parse("""{"pattern":"x"}""");

        await bridge.ExecuteAsync(args.RootElement, new Bazaar.Server.Agents.Turns.Tools.ToolCallContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), _ct);

        Assert.Equal(expected, runner.Calls[0].Taint);
        Assert.Equal(trust, runner.Calls[0].SourceTrust);
    }

    [Fact]
    public void Render_embeds_handler_json_and_reports_a_refusal_without_output_text()
    {
        var ok = CodingToolBridge.Render(new CodingToolResult(CodingToolStatus.Ok, 0, """{"matches":[]}"""));
        using (var doc = JsonDocument.Parse(ok))
        {
            Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("output").GetProperty("matches").ValueKind);
        }

        var denied = CodingToolBridge.Render(new CodingToolResult(CodingToolStatus.Denied, null, "not json", "denied", "The gate refused the call."));
        using var d = JsonDocument.Parse(denied);
        Assert.Equal("denied", d.RootElement.GetProperty("status").GetString());
        Assert.Equal("denied", d.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(JsonValueKind.String, d.RootElement.GetProperty("output").ValueKind);
    }

    private sealed class CapturingRunner : ICodingToolRunner
    {
        public List<CodingToolCall> Calls { get; } = [];

        public ValueTask<CodingToolResult> RunAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
        {
            Calls.Add(toolCall);
            return ValueTask.FromResult(new CodingToolResult(CodingToolStatus.Ok, 0, "{}"));
        }
    }

    private sealed class FixedTaint(ServerTaint taint) : ISessionTaintStore
    {
        public Task<ServerTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(taint);

        public Task SaveAsync(Guid sessionId, ServerTaint value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
