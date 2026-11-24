using System.Text.Json;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Subagents;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.GeneticMemory;
using Bazaar.Server.GeneticMemory.Abstractions;
using Bazaar.Server.Memory;

namespace Bazaar.Server.Tests.GeneticMemory;

public class GmtProposalToolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeContext(GmtChildContext? context) : IGmtChildContext
    {
        public Task<GmtChildContext?> ResolveAsync(Guid agentId, Guid sessionId, CancellationToken ct) => Task.FromResult(context);
    }

    private static readonly GmtChildContext _context = new("agent-new", false, false, GmtTerms.None);

    private static ToolCallContext Call(Guid? turn = null) => new(Guid.NewGuid(), Guid.NewGuid(), turn ?? Guid.NewGuid());

    private static JsonElement Args(string json) => JsonElement.Parse(json);

    private static bool Recorded(string result) => JsonElement.Parse(result).GetProperty("recorded").GetBoolean();

    private static GmtProposeLessonTool Tool(GmtProposalStore store, GmtChildContext? context = null, bool withContext = true, int max = 5) =>
        new(store, new GmtOptions { MaxProposalsPerChild = max }, withContext ? new FakeContext(context ?? _context) : null);

    private const string _good = """{"kind":"procedure","title":"Retry","body":"Back off on retry."}""";

    [Fact]
    public async Task A_valid_call_records_a_lesson_for_the_calling_turn_only()
    {
        var store = new GmtProposalStore();
        var turn = Guid.NewGuid();

        var result = await Tool(store).ExecuteAsync(Args(_good), Call(turn), Ct);

        Assert.True(Recorded(result));
        Assert.Empty(store.Take(Guid.NewGuid()));
        var taken = Assert.Single(store.Take(turn));
        Assert.Equal(GmtContentClass.Lesson, taken.ContentClass);
        Assert.Equal(GmtLessonKind.Procedure, taken.Kind);
    }

    [Fact]
    public async Task Without_a_session_resolver_the_tool_records_nothing()
    {
        var store = new GmtProposalStore();
        var turn = Guid.NewGuid();

        var result = await Tool(store, withContext: false).ExecuteAsync(Args(_good), Call(turn), Ct);

        Assert.False(Recorded(result));
        Assert.Empty(store.Take(turn));
    }

    [Fact]
    public async Task An_unknown_session_records_nothing()
    {
        var store = new GmtProposalStore();
        var tool = new GmtProposeLessonTool(store, new GmtOptions(), new FakeContext(null));
        var turn = Guid.NewGuid();

        Assert.False(Recorded(await tool.ExecuteAsync(Args(_good), Call(turn), Ct)));
        Assert.Empty(store.Take(turn));
    }

    [Theory]
    [InlineData("""{"kind":"preference","title":"x","body":"y"}""")]
    [InlineData("""{"kind":"tool_grant","title":"x","body":"y"}""")]
    [InlineData("""{"kind":"procedure","title":"","body":"y"}""")]
    [InlineData("""{"kind":"procedure","title":"x"}""")]
    [InlineData("""{"title":"x","body":"y"}""")]
    [InlineData("[]")]
    public async Task Bad_arguments_and_non_lesson_classes_are_refused(string json)
    {
        var store = new GmtProposalStore();
        var turn = Guid.NewGuid();

        Assert.False(Recorded(await Tool(store).ExecuteAsync(Args(json), Call(turn), Ct)));
        Assert.Empty(store.Take(turn));
    }

    [Fact]
    public async Task Too_long_a_body_is_refused()
    {
        var store = new GmtProposalStore();
        var json = JsonSerializer.Serialize(new { kind = "failure", title = "t", body = new string('x', 4001) });

        Assert.False(Recorded(await Tool(store).ExecuteAsync(Args(json), Call(), Ct)));
    }

    [Fact]
    public async Task A_child_may_record_only_the_configured_number()
    {
        var store = new GmtProposalStore();
        var tool = Tool(store, max: 2);
        var call = Call();

        Assert.True(Recorded(await tool.ExecuteAsync(Args(_good), call, Ct)));
        Assert.True(Recorded(await tool.ExecuteAsync(Args(_good), call, Ct)));
        Assert.False(Recorded(await tool.ExecuteAsync(Args(_good), call, Ct)));
        Assert.Equal(2, store.Take(call.TurnId).Count);
    }

    [Fact]
    public void The_store_forgets_the_oldest_turn_past_its_cap_and_take_empties_a_turn()
    {
        var store = new GmtProposalStore();
        var first = Guid.NewGuid();
        var p = new GmtProposal(GmtContentClass.Lesson, GmtLessonKind.Procedure, "t", "b");
        Assert.True(store.TryAdd(first, p, 5));
        for (var i = 0; i < GmtProposalStore.MaxTurns; i++)
        {
            store.TryAdd(Guid.NewGuid(), p, 5);
        }

        Assert.Empty(store.Take(first));
        var other = Guid.NewGuid();
        store.TryAdd(other, p, 5);
        Assert.Single(store.Take(other));
        Assert.Empty(store.Take(other));
    }

    [Fact]
    public void The_definition_schema_is_an_object_and_the_tool_is_named_as_registered()
    {
        Assert.Equal(GmtToolNames.ProposeLesson, GmtProposeLessonTool.Definition.Name);
        Assert.Equal("object", JsonElement.Parse(GmtProposeLessonTool.Definition.ParametersJsonSchema).GetProperty("type").GetString());
    }

    private static SubagentEnded Ended(Guid childTurn) => new(
        new SubagentRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0, ["x"], "h", "task", ["x"], "route"),
        new SubagentResult(childTurn, TurnEndReason.FinalMessage, "done", 2));

    [Fact]
    public async Task The_source_hands_over_the_recorded_lessons_once_with_the_session_facts()
    {
        var store = new GmtProposalStore();
        var child = Guid.NewGuid();
        store.TryAdd(child, new GmtProposal(GmtContentClass.Lesson, GmtLessonKind.Failure, "Leak", "Close files."), 5);
        var source = new GmtRecordedProposalSource(store, new FakeContext(new GmtChildContext("agent-new", true, false, GmtTerms.None)));

        var first = await source.ReadAsync(Ended(child), Ct);
        var second = await source.ReadAsync(Ended(child), Ct);

        Assert.NotNull(first);
        Assert.Equal("agent-new", first.TargetAgent);
        Assert.True(first.ChildTainted);
        Assert.Equal("subagent-" + child.ToString("N"), first.ChildAgent);
        Assert.Equal("turn/" + child.ToString("N"), first.TraceRef);
        Assert.Single(first.Proposals);
        Assert.Null(second);
    }

    [Fact]
    public async Task The_source_drops_lessons_when_the_session_is_unknown_or_unresolvable()
    {
        var store = new GmtProposalStore();
        var child = Guid.NewGuid();
        store.TryAdd(child, new GmtProposal(GmtContentClass.Lesson, GmtLessonKind.Failure, "Leak", "Close files."), 5);

        Assert.Null(await new GmtRecordedProposalSource(store, new FakeContext(null)).ReadAsync(Ended(child), Ct));
        store.TryAdd(child, new GmtProposal(GmtContentClass.Lesson, GmtLessonKind.Failure, "Leak", "Close files."), 5);
        Assert.Null(await new GmtRecordedProposalSource(store, null).ReadAsync(Ended(child), Ct));
    }

    [Fact]
    public async Task From_tool_call_to_the_review_queue_end_to_end()
    {
        var store = new GmtProposalStore();
        var turn = Guid.NewGuid();
        await Tool(store).ExecuteAsync(Args(_good), Call(turn), Ct);
        var service = new GeneticMemoryService(new InMemoryGmtLedger(), new GmtLessonWriter(new NullSink()), new GmtSecretScanner());
        var hook = new GmtSubagentEndHook(new GmtRecordedProposalSource(store, new FakeContext(_context)), service);

        await hook.OnSubagentEndedAsync(Ended(turn), Ct);

        var staged = Assert.Single(await service.QueueAsync(GmtReviewer.Owner, Ct));
        Assert.Equal("Retry", staged.Title);
        Assert.Equal("agent-new", staged.TargetAgent);
    }

    private sealed class NullSink : Bazaar.Server.Memory.Write.IMemoryBatchSink
    {
        public Task<Bazaar.Server.Memory.Write.MemoryCommitResult> CommitAsync(Bazaar.Server.Memory.Write.MemoryBatch batch, CancellationToken ct) =>
            Task.FromResult(Bazaar.Server.Memory.Write.MemoryCommitResult.Success("sha"));
    }
}
