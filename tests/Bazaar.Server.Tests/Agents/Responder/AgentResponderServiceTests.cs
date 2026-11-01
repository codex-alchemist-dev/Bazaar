using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Wire.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Agents.Responder;

public class AgentResponderServiceTests
{
    [Fact]
    public async Task A_notification_wakes_the_loop_and_a_missed_message_is_found_after_a_reconnect()
    {
        var ct = TestContext.Current.CancellationToken;
        var gc = new InMemoryResponderGc(ResponderTestIds.Gc);
        // Three members, so the chat is a group and the quiet message is not a DM that Charles answers on its own.
        gc.Join(ResponderTestIds.Owner);
        gc.Join(ResponderTestIds.Human);
        gc.Join(ResponderTestIds.Charles);
        var provider = new FakeProvider();
        var responder = new AgentResponder(
            gc, gc, new ScriptedGateway(provider), new PlaintextGcBodyCodec(),
            Options.Create(new AgentResponderOptions { Agents = [ResponderTestIds.CharlesPersona] }),
            NullLogger<AgentResponder>.Instance);
        var source = new ManualNotificationSource();
        var service = new AgentResponderService(source, responder, NullLogger<AgentResponderService>.Instance);

        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("Found it after the reconnect.")),
            new ScriptStep.Emit(new ModelEvent.Done(StopReason.EndTurn, "served")));

        await service.StartAsync(ct);
        try
        {
            // A chat message with no mention: the loop sees it and the lane is created, but nothing is answered.
            var quiet = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "good morning");
            source.Send(new GcNotification(ResponderTestIds.Gc, quiet));

            // A mention whose notification is lost in the connection drop.
            gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles still there?", ResponderTestIds.Charles);
            source.Send(GcNotification.ReconnectMarker);

            await WaitUntilAsync(() => gc.Messages.Any(m => m.AuthorKind == AuthorKind.Agent), ct);
        }
        finally
        {
            await service.StopAsync(ct);
        }

        var reply = Assert.Single(gc.Messages, m => m.AuthorKind == AuthorKind.Agent);
        Assert.Equal("Found it after the reconnect.", reply.Text);
        Assert.Single(provider.Calls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, ct);
        Assert.True(condition(), "The responder did not reply in time.");
    }
}
