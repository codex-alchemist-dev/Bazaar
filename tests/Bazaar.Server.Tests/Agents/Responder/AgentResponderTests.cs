using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Responder;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Write;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Tests.Agents.Responder;

public class AgentResponderTests
{
    private static readonly StopReason _stop = StopReason.EndTurn;

    private static (AgentResponder Responder, FakeProvider Provider) Create(
        InMemoryResponderGc gc, AgentResponderOptions? options = null, IEnumerable<AgentResponderPersona>? personas = null)
    {
        var provider = new FakeProvider();
        var settings = options ?? new AgentResponderOptions
        {
            Agents = (personas ?? [ResponderTestIds.CharlesPersona]).ToList(),
        };
        var responder = new AgentResponder(
            gc, gc, new ScriptedGateway(provider), new PlaintextGcBodyCodec(), Options.Create(settings), NullLogger<AgentResponder>.Instance);
        return (responder, provider);
    }

    private static InMemoryResponderGc GroupGc()
    {
        var gc = new InMemoryResponderGc(ResponderTestIds.Gc);
        gc.Join(ResponderTestIds.Owner);
        gc.Join(ResponderTestIds.Human);
        gc.Join(ResponderTestIds.Charles);
        gc.Join(ResponderTestIds.Juniper);
        return gc;
    }

    private static void Reply(FakeProvider provider, string text) =>
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta(text)),
            new ScriptStep.Emit(new ModelEvent.Done(_stop, "served")));

    private static ResponderMessage[] AgentMessages(InMemoryResponderGc gc, Guid agent) =>
        gc.Messages.Where(m => m.AuthorKind == AuthorKind.Agent && m.AuthorId == agent).ToArray();

    [Fact]
    public async Task A_mention_produces_exactly_one_reply()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        Reply(provider, "On it.");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles can you look?", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Single(provider.Calls);
        var reply = Assert.Single(AgentMessages(gc, ResponderTestIds.Charles));
        Assert.Equal("On it.", reply.Text);
        Assert.Equal(seq + 1, reply.Seq);
    }

    [Fact]
    public async Task A_repeat_trigger_does_not_double_post_and_does_not_call_the_model_again()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        Reply(provider, "Once.");
        var seq = gc.Post(AuthorKind.Human, ResponderTestIds.Human, "@Charles ping", ResponderTestIds.Charles);
        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        // A restarted process has no cursor, so it sees the same trigger again.
        var restarted = new AgentResponder(
            gc, gc, new ScriptedGateway(provider), new PlaintextGcBodyCodec(),
            Options.Create(new AgentResponderOptions { Agents = [ResponderTestIds.CharlesPersona] }),
            NullLogger<AgentResponder>.Instance);
        await restarted.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);
        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Single(provider.Calls);
        Assert.Single(AgentMessages(gc, ResponderTestIds.Charles));
    }

    [Fact]
    public async Task The_log_answers_a_repeated_reply_key_with_the_original_event()
    {
        // The log's own idempotency key is the second line of defence: the same reply key appended twice is one event.
        var gc = GroupGc();
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hi", ResponderTestIds.Charles);
        var key = AgentResponder.ReplyKey(gc.Messages[0].MessageId);
        var body = new PlaintextGcBodyCodec().Seal("first");

        var first = await gc.AppendAsync(new AppendRequest(ResponderTestIds.Gc, GcEventKinds.Message, AuthorKind.Agent, ResponderTestIds.Charles, SourceTrust.Agent, key) { ReplyToSeq = seq, Message = body }, TestContext.Current.CancellationToken);
        var second = await gc.AppendAsync(new AppendRequest(ResponderTestIds.Gc, GcEventKinds.Message, AuthorKind.Agent, ResponderTestIds.Charles, SourceTrust.Agent, key) { ReplyToSeq = seq, Message = body }, TestContext.Current.CancellationToken);

        Assert.False(first.Duplicate);
        Assert.True(second.Duplicate);
        Assert.Equal(first.Seq, second.Seq);
        Assert.Single(AgentMessages(gc, ResponderTestIds.Charles));
    }

    [Fact]
    public async Task An_agent_authored_message_triggers_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc, personas: [ResponderTestIds.CharlesPersona, ResponderTestIds.JuniperPersona]);
        var seq = gc.Post(AuthorKind.Agent, ResponderTestIds.Juniper, "@Charles what do you think?", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
        Assert.Empty(AgentMessages(gc, ResponderTestIds.Charles));
    }

    [Fact]
    public async Task An_agent_message_in_a_two_member_dm_triggers_nothing()
    {
        var gc = new InMemoryResponderGc(ResponderTestIds.Gc);
        gc.Join(ResponderTestIds.Charles);
        gc.Join(ResponderTestIds.Juniper);
        var (responder, provider) = Create(gc, personas: [ResponderTestIds.CharlesPersona]);
        var seq = gc.Post(AuthorKind.Agent, ResponderTestIds.Juniper, "Charles, status?");

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_failing_model_call_posts_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        provider.EnqueueCall(new ScriptStep.RateLimited(30));
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hello", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Single(provider.Calls);
        Assert.Empty(AgentMessages(gc, ResponderTestIds.Charles));
        Assert.Equal(seq, gc.Messages.Single().Seq);
    }

    [Fact]
    public async Task A_stream_that_drops_before_done_posts_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        provider.EnqueueCall(
            new ScriptStep.Emit(new ModelEvent.TextDelta("half a sent")),
            new ScriptStep.ConnectionDropped());
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hello", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(AgentMessages(gc, ResponderTestIds.Charles));
    }

    [Fact]
    public async Task An_empty_model_answer_posts_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        Reply(provider, "   ");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hello", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(AgentMessages(gc, ResponderTestIds.Charles));
    }

    [Fact]
    public async Task A_message_without_a_mention_in_a_group_triggers_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "anyone free this afternoon?");

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_mention_of_an_agent_who_is_not_a_member_triggers_nothing()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc, personas: [new AgentResponderPersona { AgentId = ResponderTestIds.Outsider, Name = "Outsider", SystemPrompt = "x" }]);
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Outsider hi", ResponderTestIds.Outsider);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task Any_message_in_a_two_member_dm_with_the_agent_gets_a_reply()
    {
        var gc = new InMemoryResponderGc(ResponderTestIds.Gc);
        gc.Join(ResponderTestIds.Owner);
        gc.Join(ResponderTestIds.Charles);
        var (responder, provider) = Create(gc);
        Reply(provider, "Hi, boss.");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "how is the parser going?");

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Equal("Hi, boss.", Assert.Single(AgentMessages(gc, ResponderTestIds.Charles)).Text);
    }

    [Fact]
    public async Task A_mention_of_an_agent_in_an_archived_gc_triggers_nothing()
    {
        var gc = GroupGc();
        gc.Archived = true;
        var (responder, provider) = Create(gc);
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hi", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task The_model_sees_the_system_prompt_and_the_history_with_speakers()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc, new AgentResponderOptions { Agents = [ResponderTestIds.CharlesPersona], HistoryLimit = 3 });
        Reply(provider, "Done.");
        gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "one");
        gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "two");
        gc.Post(AuthorKind.Human, ResponderTestIds.Human, "three");
        gc.Post(AuthorKind.Human, ResponderTestIds.Human, "four");
        var seq = gc.Post(AuthorKind.Human, ResponderTestIds.Human, "@Charles five", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        var request = Assert.Single(provider.Calls);
        Assert.Equal(ResponderTestIds.Charles.ToString("D"), request.AgentId);
        Assert.Equal("agent-default", request.Route);
        Assert.Equal(ModelRole.System, request.Messages[0].Role);
        Assert.Equal("You are Charles, a careful engineer.", request.Messages[0].Text);
        // The system prompt plus the last three messages, the trigger last.
        Assert.Equal(4, request.Messages.Count);
        Assert.Equal(ModelRole.User, request.Messages[1].Role);
        Assert.Equal($"human {ResponderTestIds.Human.ToString("N")[..8]}: three", request.Messages[1].Text);
        Assert.Equal($"human {ResponderTestIds.Human.ToString("N")[..8]}: @Charles five", request.Messages[3].Text);
    }

    [Fact]
    public async Task The_agent_uses_its_own_route_when_it_names_one()
    {
        var gc = GroupGc();
        var persona = new AgentResponderPersona { AgentId = ResponderTestIds.Charles, Name = "Charles", SystemPrompt = "x", Route = "custom/charles" };
        var (responder, provider) = Create(gc, personas: [persona]);
        Reply(provider, "ok");
        var seq = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles hi", ResponderTestIds.Charles);

        await responder.ProcessAsync(ResponderTestIds.Gc, seq, TestContext.Current.CancellationToken);

        Assert.Equal("custom/charles", Assert.Single(provider.Calls).Route);
    }

    [Fact]
    public async Task Two_mentions_in_turn_each_get_one_reply_in_order()
    {
        var gc = GroupGc();
        var (responder, provider) = Create(gc);
        Reply(provider, "first answer");
        Reply(provider, "second answer");
        var first = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles one", ResponderTestIds.Charles);
        await responder.ProcessAsync(ResponderTestIds.Gc, first, TestContext.Current.CancellationToken);
        var second = gc.Post(AuthorKind.Owner, ResponderTestIds.Owner, "@Charles two", ResponderTestIds.Charles);
        await responder.ProcessAsync(ResponderTestIds.Gc, second, TestContext.Current.CancellationToken);

        var replies = AgentMessages(gc, ResponderTestIds.Charles);
        Assert.Equal(["first answer", "second answer"], replies.Select(r => r.Text));
        Assert.Equal(2, provider.Calls.Count);
    }
}
