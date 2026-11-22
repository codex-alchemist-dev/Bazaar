using System.Globalization;
using System.Text;
using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Kanban;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.EndToEnd.Tests.Harness;
using Npgsql;

namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// Smoke gap 5, board step: the owner assigns a Kanban card to an agent over the wire, the assignment wakes the agent through
/// the inbox and the wake loop, and the agent works the card through the real gateway against the stub model and moves it to Review, and its reply is posted in the card's GC.
/// </summary>
public sealed class BoardTaskTests(EndToEndFixture env) : IClassFixture<EndToEndFixture>
{
    private static readonly Guid _projectId = Guid.Parse("0e2e0000-0000-4000-8000-0000000000a1");
    private static readonly Guid _cardId = Guid.Parse("0e2e0000-0000-4000-8000-0000000000c1");

    [Fact]
    public async Task An_assigned_card_wakes_the_agent_and_the_agent_moves_it_to_review()
    {
        env.RequireRunning();
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        // The card is at revision 1; the assign makes it 2, and the agent passes that version to kanban.transition.
        env.Model.QueueToolCall("kanban.transition", $$"""{"taskId":"{{_cardId:D}}","to":"review","expectedVersion":2,"reason":"The smoke note is written."}""");

        var client = new WireClient(
            new WebSocketWireTransportFactory(env.Host.WireUri), new JsonWireCodec(), new InMemoryCursorStore(),
            new WireClientOptions { DeviceId = "e2e-board", DeviceClass = "headless", ClientBuild = "0.0.0-e2e", Credential = EndToEndFixture.DevToken },
            TimeProvider.System);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var run = client.RunAsync(cts.Token);
        try
        {
            await client.SubscribeAsync(WireStreams.GcPrefix + EndToEndFixture.GcId.ToString("D"), cts.Token);
            var reply = await client.RequestAsync<KanbanAssignRequest, KanbanAssignReply>(
                WireTypes.BoardAssign, new KanbanAssignRequest(_projectId, _cardId, EndToEndFixture.AgentId, 1), cts.Token);
            Assert.Equal(KanbanAssignStatuses.Applied, reply.Status);

            string? state = null;
            for (var i = 0; i < 60 && state != "review"; i++)
            {
                await Task.Delay(500, ct);
                state = await ScalarAsync("SELECT state FROM tasks WHERE id = $1", _cardId, ct);
            }

            Assert.True(state == "review", await DiagnosticsAsync(ct));
            Assert.Equal("done", await ScalarAsync("SELECT state FROM inbox_item WHERE kind = 'assignment' AND agent_id = $1", EndToEndFixture.AgentId, ct));
            Assert.Contains(env.Model.Requests, r => r.Body.Contains("Write the smoke note", StringComparison.Ordinal));

            // The reply lands in the card's GC. The tests share that GC, so match this card's reply by the card id the stub echoes.
            var seen = new StringBuilder();
            WireGcMessage? posted = null;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            wait.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                while (posted is null)
                {
                    var message = await client.Messages.ReadAsync(wait.Token);
                    seen.AppendLine(CultureInfo.InvariantCulture, $"seen {message.AuthorId:D} {message.Text}");
                    if (message.AuthorId == EndToEndFixture.AgentId && message.Text.Contains(_cardId.ToString("D"), StringComparison.Ordinal)) posted = message;
                }
            }
            catch (OperationCanceledException) when (!cts.IsCancellationRequested)
            {
            }

            Assert.True(posted is not null, "The agent's reply never reached the card's GC.\n" + seen + await DiagnosticsAsync(ct));

            Assert.StartsWith(StubModelServer.ReplyPrefix, posted!.Text, StringComparison.Ordinal);
        }
        finally
        {
            await cts.CancelAsync();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SeedAsync(CancellationToken ct)
    {
        // Stand-ins (GAPS.md): no wire command creates a card or an agent row yet, so the harness writes them.
        await using var seed = env.Db.CreateBatch();
        seed.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO agent (agent_id, name, state) VALUES ($1, 'Smoke', 'active') ON CONFLICT DO NOTHING")
        { Parameters = { new() { Value = EndToEndFixture.AgentId } } });
        seed.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO agent_tool_grant (agent_id, tool, granted_by) VALUES ($1, 'kanban.transition', 'e2e-harness') ON CONFLICT DO NOTHING")
        { Parameters = { new() { Value = EndToEndFixture.AgentId } } });
        seed.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO tasks (id, project_id, title, state, origin_gc_id) VALUES ($1, $2, 'Write the smoke note', 'in_progress', $3)")
        { Parameters = { new() { Value = _cardId }, new() { Value = _projectId }, new() { Value = EndToEndFixture.GcId } } });
        // Review needs one link (kanban.review_min_links); a commit link stands in for the work the agent would have linked.
        seed.BatchCommands.Add(new NpgsqlBatchCommand("INSERT INTO task_links (task_id, kind, ref) VALUES ($1, 'commit', 'e2e0000000000000000000000000000000000000')")
        { Parameters = { new() { Value = _cardId } } });
        await seed.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> ScalarAsync(string sql, Guid arg, CancellationToken ct)
    {
        await using var cmd = env.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(arg);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task<string> DiagnosticsAsync(CancellationToken ct)
    {
        var sb = new StringBuilder("The card did not reach review.\n");
        foreach (var sql in new[]
        {
            "SELECT 'inbox ' || kind || ' ' || state FROM inbox_item",
            "SELECT 'task ' || state || ' v' || version FROM tasks",
            "SELECT 'gcmsg ' || author_id FROM gc_message",
            "SELECT 'turn ' || status || ' ' || wake_reason FROM turn",
            "SELECT 'step ' || n || ' ' || kind || ' ' || status || ' ' || left(coalesce(response::text, ''), 300) FROM turn_step ORDER BY started_at",
        })
        {
            await using var cmd = env.Db.CreateCommand(sql);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sb.AppendLine(reader.GetString(0));
            }
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"model calls: {env.Model.Requests.Count}");
        foreach (var line in env.Host.Log.Split('\n').Where(l => !l.Contains("WireDiscovery", StringComparison.Ordinal) && !l.Contains("wire discovery", StringComparison.Ordinal) && !l.StartsWith("info", StringComparison.Ordinal) && !l.Contains("   at ", StringComparison.Ordinal)).TakeLast(40))
        {
            sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
