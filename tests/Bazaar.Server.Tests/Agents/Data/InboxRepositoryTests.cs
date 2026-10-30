using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Data;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Agents.Data;

public sealed class InboxRepositoryTests(AgentDb db) : IClassFixture<AgentDb>
{
    private InboxRepository Inbox => new(db.DataSource);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void SkipWithoutDatabase() =>
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");

    private async Task<Guid> NewAgentAsync()
    {
        var id = Guid.CreateVersion7();
        await db.ExecAsync("INSERT INTO agent (agent_id, name, state) VALUES (@id, 'inbox test', 'active')", ("id", id));
        return id;
    }

    [Fact]
    public async Task A_duplicate_gc_item_is_a_no_op()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var gc = Guid.CreateVersion7();
        var item = new InboxEnqueue(agent, "mention", 1, gc, 42);

        Assert.True(await Inbox.EnqueueAsync(item, Ct));
        Assert.False(await Inbox.EnqueueAsync(item, Ct));
        Assert.False(await Inbox.EnqueueAsync(item with { Kind = "mention" }, Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
    }

    [Fact]
    public async Task Non_gc_items_do_not_collapse_on_null_keys()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        Assert.True(await Inbox.EnqueueAsync(new InboxEnqueue(agent, "scheduled", 4), Ct));
        Assert.True(await Inbox.EnqueueAsync(new InboxEnqueue(agent, "scheduled", 4), Ct));
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
    }

    [Fact]
    public async Task A_source_ref_dedupes_only_within_its_kind_and_agent()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        var other = await NewAgentAsync();

        Assert.True(await Inbox.EnqueueAsync(new InboxEnqueue(agent, "vote_request", 3, SourceRef: "ballot-7"), Ct));
        Assert.False(await Inbox.EnqueueAsync(new InboxEnqueue(agent, "vote_request", 3, SourceRef: "ballot-7"), Ct));
        Assert.True(await Inbox.EnqueueAsync(new InboxEnqueue(agent, "assignment", 2, SourceRef: "ballot-7"), Ct));
        Assert.True(await Inbox.EnqueueAsync(new InboxEnqueue(other, "vote_request", 3, SourceRef: "ballot-7"), Ct));
    }

    [Fact]
    public async Task A_gc_item_needs_both_gc_id_and_seq()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, GcId: Guid.CreateVersion7()), Ct));
    }

    [Fact]
    public async Task Claim_takes_the_highest_priority_due_items_first()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "gc_digest", 7, Guid.CreateVersion7(), 1), Ct);
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, Guid.CreateVersion7(), 2), Ct);
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "interrupt", 0, SourceRef: "push-1"), Ct);
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "scheduled", 4, DueAt: DateTimeOffset.UtcNow.AddHours(1)), Ct);

        var claimed = await Inbox.ClaimAsync(agent, "turn-1", 2, Ct);

        Assert.Equal(["interrupt", "mention"], claimed.Select(r => r.Kind));
        Assert.All(claimed, r => Assert.True(r.Priority is 0 or 1));
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Equal(2, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
    }

    [Fact]
    public async Task A_claimed_item_is_not_claimed_again_and_complete_removes_it_from_the_queue()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, SourceRef: "dm-9"), Ct);

        var first = await Inbox.ClaimAsync(agent, "turn-a", 5, Ct);
        var second = await Inbox.ClaimAsync(agent, "turn-b", 5, Ct);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(1, await Inbox.CompleteAsync("turn-a", first.Select(r => r.InboxItemId).ToArray(), Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Done, Ct));
        Assert.Equal(0, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
    }

    [Fact]
    public async Task Complete_by_another_holder_changes_nothing()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, SourceRef: "dm-10"), Ct);
        var claimed = await Inbox.ClaimAsync(agent, "turn-a", 5, Ct);

        Assert.Equal(0, await Inbox.CompleteAsync("turn-b", claimed.Select(r => r.InboxItemId).ToArray(), Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Claimed, Ct));
    }

    [Fact]
    public async Task Release_returns_a_failed_turns_items_to_pending()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "assignment", 2, SourceRef: "card-3"), Ct);
        await Inbox.ClaimAsync(agent, "turn-crash", 5, Ct);

        Assert.Equal(1, await Inbox.ReleaseAsync("turn-crash", Ct));
        Assert.Equal(1, await Inbox.CountAsync(agent, InboxState.Pending, Ct));
        Assert.Single(await Inbox.ClaimAsync(agent, "turn-next", 5, Ct));
    }

    [Fact]
    public async Task Items_due_in_the_future_are_not_claimed()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        await Inbox.EnqueueAsync(new InboxEnqueue(agent, "scheduled", 4, DueAt: DateTimeOffset.UtcNow.AddDays(1)), Ct);

        Assert.Empty(await Inbox.ClaimAsync(agent, "turn-a", 5, Ct));
    }

    [Fact]
    public async Task Concurrent_claims_never_share_an_item()
    {
        SkipWithoutDatabase();
        var agent = await NewAgentAsync();
        for (var i = 0; i < 20; i++)
            await Inbox.EnqueueAsync(new InboxEnqueue(agent, "mention", 1, Guid.CreateVersion7(), i + 1), Ct);

        var batches = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Inbox.ClaimAsync(agent, $"claim-{i}", 5, Ct)));
        var ids = batches.SelectMany(b => b.Select(r => r.InboxItemId)).ToList();

        Assert.Equal(20, ids.Count);
        Assert.Equal(20, ids.Distinct().Count());
    }
}
