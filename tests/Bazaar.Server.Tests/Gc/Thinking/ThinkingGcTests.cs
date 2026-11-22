using System.Text;
using Bazaar.Contracts.Gateway;
using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Gc;
using Bazaar.Server.Gc.Body;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.Gc.Write;
using Bazaar.Server.Tests.Agents.Turns;
using Bazaar.Server.ThinkingGc;
using Npgsql;
using static Bazaar.Server.Tests.Agents.Turns.ScriptedModelGateway;

namespace Bazaar.Server.Tests.Gc.Thinking;

/// <summary>
/// The Thinking GC writer (docs/design/01 Thinking GCs [DECIDED, round 3]): nesting under the working GC, owner read, peer denial,
/// idempotent entries, and a real turn writing its reasoning and steps there.
/// </summary>
public sealed class ThinkingGcTests(GcDb db) : IClassFixture<GcDb>
{
    private static readonly Guid _owner = Guid.Parse("00000000-0000-7000-8000-0000000000aa");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ThinkingGcStore Store => new(db.DataSource);

    private ThinkingGcRecorder Recorder() => new(Store, new GcLogWriter(db.DataSource, new RecordingNotifier()), new PlaintextGcBodyCodec());

    private async Task<Guid> WorkingGcAsync(Guid? member, string typeKey = "general", bool archived = false)
    {
        var id = Guid.CreateVersion7();
        await ExecAsync("INSERT INTO gc (gc_id, type_key, title, created_by, archived_at) VALUES (@id, @type, 'work', @owner, CASE WHEN @archived THEN now() END)",
            ("id", id), ("type", typeKey), ("owner", _owner), ("archived", archived));
        if (member is { } m)
            await ExecAsync("INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES (@id, @m, 'member', 0)", ("id", id), ("m", m));
        return id;
    }

    private async Task ExecAsync(string sql, params (string Name, object Value)[] args)
    {
        await using var cmd = db.DataSource.CreateCommand(sql);
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<List<string>> BodiesAsync(Guid gc)
    {
        await using var cmd = db.DataSource.CreateCommand("SELECT body_ct FROM gc_message_version WHERE gc_id = @gc ORDER BY seq");
        cmd.Parameters.AddWithValue("gc", gc);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var bodies = new List<string>();
        while (await reader.ReadAsync(Ct))
            bodies.Add(Encoding.UTF8.GetString((byte[])reader[0]));
        return bodies;
    }

    [Fact]
    public async Task Ensure_creates_one_nested_thinking_gc_with_the_agent_as_its_member()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();
        var working = await WorkingGcAsync(agent);

        var first = await Store.EnsureAsync(working, agent, Ct);
        var second = await Store.EnsureAsync(working, agent, Ct);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(ThinkingGcStore.DeterministicId(working, agent), first);
        Assert.Equal("thinking", await db.ScalarAsync<string>("SELECT type_key FROM gc WHERE gc_id = $1", first!.Value));
        Assert.Equal(working, await db.ScalarAsync<Guid>("SELECT parent_gc_id FROM gc WHERE gc_id = $1", first.Value));
        Assert.Equal(1L, await db.ScalarAsync<long>("SELECT count(*) FROM gc WHERE parent_gc_id = $1", working));
        Assert.Equal(agent, await db.ScalarAsync<Guid>("SELECT principal_id FROM gc_member WHERE gc_id = $1", first.Value));
    }

    [Fact]
    public async Task Two_agents_in_one_gc_get_separate_thinking_gcs()
    {
        GcTestSkip.WithoutDatabase();
        var a = Guid.CreateVersion7();
        var b = Guid.CreateVersion7();
        var working = await WorkingGcAsync(a);
        await ExecAsync("INSERT INTO gc_member (gc_id, principal_id, role, joined_seq) VALUES (@id, @m, 'member', 0)", ("id", working), ("m", b));

        var ga = await Store.EnsureAsync(working, a, Ct);
        var gb = await Store.EnsureAsync(working, b, Ct);

        Assert.NotEqual(ga, gb);
    }

    [Fact]
    public async Task Ensure_refuses_a_non_member_an_archived_gc_a_thinking_gc_and_an_unknown_gc()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();

        Assert.Null(await Store.EnsureAsync(await WorkingGcAsync(null), agent, Ct));
        Assert.Null(await Store.EnsureAsync(await WorkingGcAsync(agent, archived: true), agent, Ct));
        Assert.Null(await Store.EnsureAsync(await WorkingGcAsync(agent, typeKey: "thinking"), agent, Ct));
        Assert.Null(await Store.EnsureAsync(Guid.CreateVersion7(), agent, Ct));
    }

    [Fact]
    public async Task The_owner_reads_the_thinking_gc_and_other_agents_and_humans_do_not()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();
        var working = await WorkingGcAsync(agent);
        var thinking = (await Store.EnsureAsync(working, agent, Ct))!.Value;
        var access = new GcReadAccess(db.DataSource);

        Assert.Equal(GcReadGrant.Full, await access.GetGrantAsync(thinking, new GcPrincipal(_owner, PrincipalKind.Owner, "owner"), Ct));
        Assert.Equal(GcReadGrant.Full, await access.GetGrantAsync(thinking, new GcPrincipal(agent, PrincipalKind.Agent, "agent"), Ct));
        Assert.Equal(GcReadGrant.Denied, await access.GetGrantAsync(thinking, new GcPrincipal(Guid.CreateVersion7(), PrincipalKind.Agent, "peer"), Ct));
        Assert.Equal(GcReadGrant.Denied, await access.GetGrantAsync(thinking, new GcPrincipal(Guid.CreateVersion7(), PrincipalKind.Human, "human"), Ct));
    }

    [Fact]
    public async Task Recording_writes_an_agent_message_and_a_repeat_writes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();
        var working = await WorkingGcAsync(agent);
        var entry = new ThinkingEntry(agent, Guid.CreateVersion7(), working.ToString("D"), 1, ThinkingEntryKind.Reasoning, "Considering the options.");

        Assert.True(await Recorder().RecordAsync(entry, Ct));
        Assert.True(await Recorder().RecordAsync(entry, Ct));

        var thinking = ThinkingGcStore.DeterministicId(working, agent);
        Assert.Equal(["Considering the options."], await BodiesAsync(thinking));
        Assert.Equal("agent", await db.ScalarAsync<string>("SELECT author_kind FROM gc_event WHERE gc_id = $1", thinking));
        // The working GC itself gets nothing: thinking never lands in the room.
        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc_event WHERE gc_id = $1", working));
    }

    [Fact]
    public async Task Recording_without_a_usable_working_gc_writes_nothing()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();
        var turn = Guid.CreateVersion7();

        Assert.False(await Recorder().RecordAsync(new ThinkingEntry(agent, turn, null, 1, ThinkingEntryKind.Reasoning, "x"), Ct));
        Assert.False(await Recorder().RecordAsync(new ThinkingEntry(agent, turn, "not-a-guid", 1, ThinkingEntryKind.Reasoning, "x"), Ct));
        Assert.False(await Recorder().RecordAsync(
            new ThinkingEntry(agent, turn, (await WorkingGcAsync(null)).ToString("D"), 1, ThinkingEntryKind.Reasoning, "x"), Ct));
    }

    [Fact]
    public async Task Long_text_is_split_into_consecutive_messages_and_never_cut()
    {
        GcTestSkip.WithoutDatabase();
        var agent = Guid.CreateVersion7();
        var working = await WorkingGcAsync(agent);
        var text = new string('a', GcLimits.MaxTextLength) + new string('b', 10);

        await Recorder().RecordAsync(new ThinkingEntry(agent, Guid.CreateVersion7(), working.ToString("D"), 1, ThinkingEntryKind.Reasoning, text), Ct);

        var bodies = await BodiesAsync(ThinkingGcStore.DeterministicId(working, agent));
        Assert.Equal(2, bodies.Count);
        Assert.Equal(text, string.Concat(bodies));
    }

    [Fact]
    public void Split_does_not_break_a_surrogate_pair()
    {
        var parts = ThinkingGcRecorder.Split("ab\U0001F600cd", 3);

        Assert.Equal("ab\U0001F600cd", string.Concat(parts));
        Assert.All(parts, p => Assert.False(char.IsHighSurrogate(p[^1])));
    }

    [Fact]
    public async Task A_turn_writes_its_reasoning_tool_step_and_end_to_the_agents_thinking_gc()
    {
        GcTestSkip.WithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("thinker", null, Ct);
        var working = await WorkingGcAsync(agent);
        var gateway = new ScriptedModelGateway()
            .Reply(new ModelEvent.ThinkingDelta("I should look it up. "), new ModelEvent.ThinkingDelta("Then answer."),
                new ModelEvent.ToolCallStart("c1", "lookup"), new ModelEvent.ToolCallArgsDelta("c1", "{}"), new ModelEvent.ToolCallEnd("c1"),
                UsageEvent(10, 5), DoneEvent(StopReason.ToolUse))
            .Reply(TextEvent("Done."), UsageEvent(10, 5), DoneEvent(StopReason.EndTurn));
        var runner = new TurnRunner(db.DataSource, gateway, new RecordingToolDispatcher(), thinking: Recorder());

        var result = await runner.RunAsync(
            new TurnRequest(agent, Guid.CreateVersion7(), "mention", "r", [new ModelMessage(ModelRole.User, "hi")], TriggerGcId: working.ToString("D")), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var bodies = await BodiesAsync(ThinkingGcStore.DeterministicId(working, agent));
        Assert.Equal(3, bodies.Count);
        Assert.Equal("I should look it up. Then answer.", bodies[0]);
        Assert.StartsWith("Tool lookup:", bodies[1]);
        Assert.Contains("completed", bodies[2]);
    }

    [Fact]
    public async Task A_turn_with_no_recorder_or_no_working_gc_writes_no_thinking_gc()
    {
        GcTestSkip.WithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("quiet", null, Ct);
        var working = await WorkingGcAsync(agent);
        TurnRequest Req(string? gc) => new(agent, Guid.CreateVersion7(), "mention", "r", [new ModelMessage(ModelRole.User, "hi")], TriggerGcId: gc);

        var plain = new TurnRunner(db.DataSource, new ScriptedModelGateway().Reply(FinalAnswer("a")), new RecordingToolDispatcher());
        var noGc = new TurnRunner(db.DataSource, new ScriptedModelGateway().Reply(FinalAnswer("b")), new RecordingToolDispatcher(), thinking: Recorder());

        Assert.Equal(TurnOutcome.Completed, (await plain.RunAsync(Req(working.ToString("D")), Ct)).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await noGc.RunAsync(Req(null), Ct)).Outcome);

        Assert.Equal(0L, await db.ScalarAsync<long>("SELECT count(*) FROM gc WHERE parent_gc_id = $1", working));
    }

    [Fact]
    public async Task A_failing_recorder_does_not_fail_the_turn()
    {
        GcTestSkip.WithoutDatabase();
        var agent = await new AgentRepository(db.DataSource).CreateAsync("resilient", null, Ct);
        var working = await WorkingGcAsync(agent);
        var runner = new TurnRunner(db.DataSource, new ScriptedModelGateway().Reply(FinalAnswer("ok")), new RecordingToolDispatcher(), thinking: new ThrowingRecorder());

        var result = await runner.RunAsync(
            new TurnRequest(agent, Guid.CreateVersion7(), "mention", "r", [new ModelMessage(ModelRole.User, "hi")], TriggerGcId: working.ToString("D")), Ct);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
    }

    private sealed class ThrowingRecorder : IThinkingRecorder
    {
        public Task<bool> RecordAsync(ThinkingEntry entry, CancellationToken cancellationToken) => throw new NpgsqlException("down");
    }
}
