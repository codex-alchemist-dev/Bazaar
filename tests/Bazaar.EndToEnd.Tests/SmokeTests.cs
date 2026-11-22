using Bazaar.Client.Abstractions.Wire;
using Bazaar.Contracts.Wire;
using Bazaar.Contracts.Wire.Codec;
using Bazaar.EndToEnd.Tests.Harness;
using Npgsql;

namespace Bazaar.EndToEnd.Tests;

/// <summary>
/// The first end-to-end path (docs/design/Bazaar.md "install, sign in, create a company, agents do real work"; push plan
/// 2026-11-30, critical path). Boots the real server process on Postgres, signs a headless client in over the wire, and makes
/// one agent answer through the real gateway against a keyless stub model. Steps that cannot run yet are listed in GAPS.md.
/// </summary>
public sealed class SmokeTests(EndToEndFixture env) : IClassFixture<EndToEndFixture>
{
    private static WireClient NewClient(EndToEndFixture env, string credential) => new(
        new WebSocketWireTransportFactory(env.Host.WireUri),
        new JsonWireCodec(),
        new InMemoryCursorStore(),
        new WireClientOptions { DeviceId = "e2e-device", DeviceClass = "headless", ClientBuild = "0.0.0-e2e", Credential = credential },
        TimeProvider.System);

    [Fact]
    public async Task The_server_boots_on_a_migrated_database_and_reports_ready()
    {
        env.RequireRunning();
        using var http = new HttpClient { BaseAddress = env.Host.HttpBase };

        var live = await http.GetAsync("health/live", TestContext.Current.CancellationToken);
        var ready = await http.GetAsync("health/ready", TestContext.Current.CancellationToken);

        Assert.True(live.IsSuccessStatusCode);
        Assert.True(ready.IsSuccessStatusCode, await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_wrong_token_is_refused_and_the_client_stops()
    {
        env.RequireRunning();
        var client = NewClient(env, "not-the-token");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(20));

        var run = client.RunAsync(cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        Assert.Equal(WireClientStopReason.AuthFailed, client.Stopped);
        Assert.Same(run, finished);
    }

    [Fact]
    public async Task The_owner_signs_in_sends_a_mention_and_the_agent_answers_through_the_gateway()
    {
        env.RequireRunning();
        var client = NewClient(env, EndToEndFixture.DevToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var run = client.RunAsync(cts.Token);
        await client.SubscribeAsync(WireStreams.GcPrefix + EndToEndFixture.GcId.ToString("D"), cts.Token);

        const string question = "Say hello to the smoke test.";
        var sent = await client.RequestAsync<GcSend, GcSendResult>(
            WireTypes.GcSend,
            new GcSend(EndToEndFixture.GcId, question, [EndToEndFixture.AgentId], null),
            cts.Token);
        Assert.True(sent.N >= 1);

        WireGcMessage? reply = null;
        while (reply is null)
        {
            var message = await client.Messages.ReadAsync(cts.Token);
            // The tests share one GC and run in parallel, so match the reply to this test's own question.
            if (message.AuthorId == EndToEndFixture.AgentId && message.Text.Contains(question, StringComparison.Ordinal)) reply = message;
        }

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        Assert.StartsWith(StubModelServer.ReplyPrefix, reply.Text, StringComparison.Ordinal);
        Assert.Contains(question, reply.Text, StringComparison.Ordinal);

        // The agent reached the model through the gateway with the unified key, once.
        var call = Assert.Single(env.Model.Requests, r => r.LastUserMessage.Contains(question, StringComparison.Ordinal));
        Assert.Equal("Bearer " + EndToEndFixture.FreeLlmKey, call.Authorization);
        Assert.Contains(question, call.LastUserMessage, StringComparison.Ordinal);

        // The reply is in the append-only log.
        await using var count = env.Db.CreateCommand("SELECT count(*) FROM gc_message WHERE gc_id = $1 AND author_id = $2");
        count.Parameters.AddWithValue(EndToEndFixture.GcId);
        count.Parameters.AddWithValue(EndToEndFixture.AgentId);
        // At least one: the other test replies in the same GC, in parallel.
        Assert.True((long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))! >= 1);
    }

    [Fact]
    public async Task The_model_call_is_metered_as_pooled_usage_for_the_agent()
    {
        env.RequireRunning();
        var client = NewClient(env, EndToEndFixture.DevToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var run = client.RunAsync(cts.Token);
        await client.SubscribeAsync(WireStreams.GcPrefix + EndToEndFixture.GcId.ToString("D"), cts.Token);
        await client.RequestAsync<GcSend, GcSendResult>(
            WireTypes.GcSend, new GcSend(EndToEndFixture.GcId, "Count my tokens.", [EndToEndFixture.AgentId], null), cts.Token);
        while (true)
        {
            var message = await client.Messages.ReadAsync(cts.Token);
            if (message.AuthorId == EndToEndFixture.AgentId && message.Text.Contains("Count my tokens.", StringComparison.Ordinal)) break;
        }

        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        // The usage row is written after the reply is posted, so give it a moment.
        var rows = "";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            await using var usage = env.Db.CreateCommand("SELECT agent_id, status, identity, input, output, error_code FROM gw_usage");
            await using var reader = await usage.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var seen = new List<string>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                var row = $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetInt64(3)}|{reader.GetInt64(4)}|{(reader.IsDBNull(5) ? "" : reader.GetString(5))}";
                seen.Add(row);
                if (row.StartsWith(EndToEndFixture.AgentId.ToString("D") + "|success|pooled|", StringComparison.Ordinal) && reader.GetInt64(3) > 0 && reader.GetInt64(4) > 0)
                {
                    return;
                }
            }

            rows = string.Join('\n', seen);
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        Assert.Fail("No successful pooled usage row for the agent. gw_usage holds:\n" + rows);
    }

    [Fact]
    public void The_host_log_has_no_errors()
    {
        env.RequireRunning();
        var bad = env.Host.Log.Split('\n').Where(l => l.StartsWith("fail:", StringComparison.Ordinal) || l.StartsWith("crit:", StringComparison.Ordinal)).ToList();

        Assert.True(bad.Count == 0, string.Join('\n', bad) + "\n--- host log ---\n" + env.Host.Log);
    }
}
