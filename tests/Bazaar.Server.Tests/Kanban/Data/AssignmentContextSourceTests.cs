using Bazaar.RyuseiFuda.Kanban;
using Bazaar.RyuseiFuda.Kanban.Data;
using Bazaar.Server.Agents;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Context;
using Bazaar.Testing.Postgres;

namespace Bazaar.Server.Tests.Kanban.Data;

/// <summary>What a turn woken by a card assignment shows the model (doc 12, Assignment wake).</summary>
public sealed class AssignmentContextSourceTests(KanbanDb db) : IClassFixture<KanbanDb>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Guid Agent, Guid Card, Guid Inbox)> SeedAsync(string sourceRef, string kind = "assignment")
    {
        var (gc, _) = await db.CreateGcWithMessageAsync();
        var card = Guid.CreateVersion7();
        var agent = Guid.CreateVersion7();
        var inbox = Guid.CreateVersion7();
        await using (var conn = await db.DataSource.OpenConnectionAsync(Ct))
        await using (var tx = await conn.BeginTransactionAsync(Ct))
        {
            await new TaskRepository(db.DataSource).InsertAsync(tx,
                new NewTask(card, Guid.CreateVersion7(), "Write the note", TaskState.Ready, null, TaskGate.None, gc, null, null), Ct);
            await tx.CommitAsync(Ct);
        }

        await using var cmd = db.DataSource.CreateBatch();
        cmd.BatchCommands.Add(new Npgsql.NpgsqlBatchCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'assignee', 'active')") { Parameters = { new() { Value = agent } } });
        cmd.BatchCommands.Add(new Npgsql.NpgsqlBatchCommand("INSERT INTO inbox_item (inbox_item_id, agent_id, kind, source_ref, priority) VALUES ($1, $2, $3, $4, 3)")
        { Parameters = { new() { Value = inbox }, new() { Value = agent }, new() { Value = kind }, new() { Value = sourceRef.Replace("{card}", card.ToString("D"), StringComparison.Ordinal) } } });
        await cmd.ExecuteNonQueryAsync(Ct);
        return (agent, card, inbox);
    }

    private static TurnContextRequest Request(Guid agent, Guid inbox) =>
        new(agent, Guid.CreateVersion7(), TurnRole.Actor, "assignment", [inbox]);

    [Fact]
    public async Task An_assignment_wake_shows_the_card_as_task_state_and_as_the_wake()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var (agent, card, inbox) = await SeedAsync("kanban:assign:{card}:2");

        var items = await new AssignmentContextSource(db.DataSource).ReadAsync(Request(agent, inbox), Ct);

        Assert.Equal([ContextSection.TaskState, ContextSection.Inbox], items.Select(i => i.Section).ToArray());
        Assert.All(items, i => Assert.Contains("Write the note", i.Text, StringComparison.Ordinal));
        Assert.All(items, i => Assert.Equal(card.ToString("D"), i.ItemId));
        Assert.Contains("ready", items[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_wakes_and_unknown_cards_show_nothing()
    {
        Assert.SkipWhen(PgSource.IsUnsupportedHost, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        var source = new AssignmentContextSource(db.DataSource);

        var (agent, _, inbox) = await SeedAsync("kanban:assign:{card}:2", kind: "mention");
        Assert.Empty(await source.ReadAsync(Request(agent, inbox), Ct));

        var (agent2, _, inbox2) = await SeedAsync($"kanban:assign:{Guid.NewGuid():D}:2");
        Assert.Empty(await source.ReadAsync(Request(agent2, inbox2), Ct));

        Assert.Empty(await source.ReadAsync(new TurnContextRequest(agent, Guid.CreateVersion7(), TurnRole.Actor, "assignment", []), Ct));
    }
}
