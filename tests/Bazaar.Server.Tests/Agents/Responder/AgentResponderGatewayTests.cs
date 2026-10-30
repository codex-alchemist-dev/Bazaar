using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Tests.Gateway.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Agents.Responder;

/// <summary>
/// The agent responder through the real <see cref="ModelGateway"/> pipeline: route resolution against agent policy, the
/// fallback chain, and the scripted <see cref="FakeProvider"/> behind the adapters. Nothing touches the network. The
/// gateway wiring is the one <see cref="GatewayHarness"/> uses for the gateway's own tests.
/// </summary>
public class AgentResponderGatewayTests
{
    private const string _route = "r";
    private static readonly StopReason _stop = StopReason.EndTurn;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A GC with the owner, a human and the agent, so the owner's DM with the agent is a two-member GC.</summary>
    private static InMemoryResponderGc DirectMessageGc()
    {
        var gc = new InMemoryResponderGc(ResponderTestIds.Gc);
        gc.Join(ResponderTestIds.Owner);
        gc.Join(ResponderTestIds.Charles);
        return gc;
    }

    /// <summary>The gateway harness with the agent's policy set. The route <c>r</c> is <c>a:m</c>, then <c>b:m</c>.</summary>
    private static GatewayHarness Harness(string[] entries, string defaultRoute = _route)
    {
        var harness = new GatewayHarness();
        harness.World.AddPolicy(ResponderTestIds.Charles.ToString("D"), entries, defaultRoute);
        return harness;
    }

    private static AgentResponder Responder(GatewayHarness harness, InMemoryResponderGc gc) =>
        new(
            gc,
            gc,
            harness.Gateway(),
            new PlaintextGcBodyCodec(),
            Options.Create(new AgentResponderOptions
            {
                Agents = [ResponderTestIds.CharlesPersona],
                DefaultRoute = _route,
            }),
            NullLogger<AgentResponder>.Instance);

    private static void Reply(FakeProvider provider, string text) =>
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta(text)),
            new ScriptStep.Emit(new ModelEvent.Done(_stop, "served")));

    private static ResponderMessage[] AgentMessages(InMemoryResponderGc gc) =>
        gc.Messages.Where(m => m.AuthorKind == AuthorKind.Agent && m.AuthorId == ResponderTestIds.Charles).ToArray();

    [Fact]
    public async Task A_dm_from_the_owner_produces_one_reply_through_the_gateway_with_one_provider_request()
    {
        var gc = DirectMessageGc();
        var harness = Harness(["a:m", "b:m", _route]);
        var provider = harness.Adapter("a").Provider;
        Reply(provider, "Hello from the gateway.");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "Are you there?");

        await Responder(harness, gc).ProcessAsync(ResponderTestIds.Gc, seq, Ct);

        var request = Assert.Single(provider.Calls);
        Assert.Equal(ResponderTestIds.Charles.ToString("D"), request.AgentId);
        Assert.Equal(_route, request.Route);
        Assert.Empty(harness.Adapter("b").Provider.Calls);

        var reply = Assert.Single(AgentMessages(gc));
        Assert.Equal("Hello from the gateway.", reply.Text);
        Assert.Equal(seq + 1, reply.Seq);
    }

    [Fact]
    public async Task An_agent_whose_policy_denies_the_route_produces_no_reply_and_no_provider_call()
    {
        var gc = DirectMessageGc();
        // The agent is allowed only on c:m, which is outside route r, so its default route is c:m and r is not allowed.
        var harness = Harness(["c:m"], defaultRoute: "c:m");
        var provider = harness.Adapter("a").Provider;
        Reply(provider, "This must never be posted.");
        var messagesBefore = gc.Messages.Count;

        // The gateway itself refuses the call before any provider is chosen.
        var denied = await GatewayHarness.CollectAsync(
            harness.Gateway().CallAsync(new ModelRequest(ResponderTestIds.Charles.ToString("D"), _route, [new ModelMessage(ModelRole.User, "hi")], []), Ct),
            Ct);
        var failure = Assert.IsType<ModelEvent.Failure>(Assert.Single(denied));
        Assert.Equal("policy_denied", failure.Code);
        Assert.Empty(provider.Calls);

        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "Are you there?");
        await Responder(harness, gc).ProcessAsync(ResponderTestIds.Gc, seq, Ct);

        Assert.Empty(provider.Calls);
        Assert.Empty(AgentMessages(gc));
        Assert.Equal(messagesBefore + 1, gc.Messages.Count);
    }

    [Fact]
    public async Task Handling_the_same_trigger_twice_posts_one_reply()
    {
        var gc = DirectMessageGc();
        var harness = Harness(["a:m", "b:m", _route]);
        var provider = harness.Adapter("a").Provider;
        Reply(provider, "Once.");
        // A second script that must stay unplayed: a repeated trigger may not spend a model call.
        Reply(provider, "Twice.");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "Ping");

        await Responder(harness, gc).ProcessAsync(ResponderTestIds.Gc, seq, Ct);

        // A restarted process has no cursor, so it sees the same trigger again.
        await Responder(harness, gc).ProcessAsync(ResponderTestIds.Gc, seq, Ct);

        Assert.Single(provider.Calls);
        Assert.Equal(1, provider.RemainingScripts);
        var reply = Assert.Single(AgentMessages(gc));
        Assert.Equal("Once.", reply.Text);
    }
}
