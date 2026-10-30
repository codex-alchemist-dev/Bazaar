using Bazaar.Contracts.Wire;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Fakes;
using Bazaar.Testing.Postgres;
using Microsoft.Extensions.AI;

namespace Bazaar.ExitCheck.Tests;

/// <summary>
/// The Stage 3 exit check (plan Batch 3, E2E; doc 34 layer L3): a fake agent posts to a GC, a client drops its socket and
/// replays with no loss and no duplicate, and a model call outside policy is denied. Real PostgreSQL, real host, real
/// client. Never skipped: while server pieces are unmerged the server tests fail with the list in <see cref="ExitCheckLedger"/>.
/// </summary>
public sealed class V03ExitCheckTests : IAsyncLifetime
{
    private sealed class Db : PgFixture;

    private readonly Db _db = new();
    private readonly FakeProvider _provider = new();
    private readonly ExitCheckPolicyWorld _policy = new();
    private static readonly bool _hasDb = !PgSource.IsUnsupportedHost;
    private IExitCheckServer? _server;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        // Windows without BAZAAR_TEST_PG has no database by design (test.ci.windows_db_job = false); Linux never skips.
        if (_hasDb) await _db.InitializeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        if (_hasDb) await _db.DisposeAsync();
    }

    private PolicyGateGateway Gateway() => new(_policy.Resolver, _provider);

    private async Task<(ExitCheckDevice Human, ScriptedAgent Agent, ExitCheckDevice AgentDevice)> StartAsync()
    {
        Assert.SkipWhen(!_hasDb, "Windows without BAZAAR_TEST_PG: no test PostgreSQL available.");
        _server = await ExitCheckServers.StartAsync(_db);
        var human = new ExitCheckDevice(_server, _server.HumanId, "human-phone");
        var agentDevice = new ExitCheckDevice(_server, _server.AgentId, "agent-runtime");
        var stream = WireStreams.Gc(_server.GcId);
        await human.Client.SubscribeAsync(stream, Ct);
        await agentDevice.Client.SubscribeAsync(stream, Ct);
        var agent = new ScriptedAgent(_server.GcId, agentDevice.Client, _provider, Gateway(), ExitCheckPolicyWorld.AllowedModel);
        return (human, agent, agentDevice);
    }

    [Fact]
    public async Task Agent_posts_and_a_subscribed_client_sees_every_message_once_in_order()
    {
        var (human, agent, agentDevice) = await StartAsync();
        await using var h = human;
        await using var a = agentDevice;

        for (var i = 1; i <= 5; i++) await agent.SayAsync($"message {i}", Ct);

        var seen = await human.ReadAsync(5, Ct);
        Assert.Equal(["message 1", "message 2", "message 3", "message 4", "message 5"], seen.Select(m => m.Text));
        Assert.All(seen, m => Assert.Equal(_server!.AgentId, m.AuthorId));
        Assert.Equal(seen.Select(m => m.N).Order(), seen.Select(m => m.N));
        Assert.Equal(seen.Count, seen.Select(m => m.N).Distinct().Count());
    }

    [Fact]
    public async Task A_dropped_socket_replays_the_missed_messages_with_no_loss_and_no_duplicate()
    {
        var (human, agent, agentDevice) = await StartAsync();
        await using var h = human;
        await using var a = agentDevice;

        for (var i = 1; i <= 3; i++) await agent.SayAsync($"before {i}", Ct);
        var before = await human.ReadAsync(3, Ct);

        Assert.True(await human.Transport.SeverAsync());
        for (var i = 1; i <= 4; i++) await agent.SayAsync($"while away {i}", Ct);

        var replayed = await human.ReadAsync(4, Ct);
        Assert.Equal(["while away 1", "while away 2", "while away 3", "while away 4"], replayed.Select(m => m.Text));
        Assert.True(human.Transport.Opened >= 2, "the client must have reconnected");

        var all = before.Concat(replayed).ToList();
        Assert.Equal(all.Select(m => m.N).Distinct().Count(), all.Count);
        Assert.Equal(all.Select(m => m.N).Order(), all.Select(m => m.N));
        Assert.All(all.Zip(all.Skip(1)), pair => Assert.Equal(pair.First.N + 1, pair.Second.N));
        Assert.True(await human.StaysQuietAsync(TimeSpan.FromMilliseconds(500), Ct), "a duplicate or extra entry arrived after replay");

        // The server's own history agrees: exactly these seven messages, nothing lost on either side.
        var page = await human.Client.RequestAsync<GcHistory, GcPage>(WireTypes.GcHistory, new GcHistory(_server!.GcId, all[^1].N + 1, 50), Ct);
        Assert.Equal(all.Select(m => m.Text), page.Items.Select(m => m.Text));
    }

    [Fact]
    public async Task A_retried_send_over_a_dropped_socket_posts_once()
    {
        var (human, agent, agentDevice) = await StartAsync();
        await using var h = human;
        await using var a = agentDevice;

        var send = agent.SayAsync("only once", Ct);
        await agentDevice.Transport.SeverAsync();
        await send;

        var seen = await human.ReadAsync(1, Ct);
        Assert.Equal("only once", seen[0].Text);
        Assert.True(await human.StaysQuietAsync(TimeSpan.FromMilliseconds(500), Ct));
    }

    [Fact]
    public async Task A_model_call_outside_policy_is_denied_and_nothing_is_posted()
    {
        var (human, _, agentDevice) = await StartAsync();
        await using var h = human;
        await using var a = agentDevice;
        var denied = new ScriptedAgent(_server!.GcId, agentDevice.Client, _provider, Gateway(), ExitCheckPolicyWorld.DeniedModel);

        var ex = await Assert.ThrowsAsync<GatewayModelException>(() => denied.SayAsync("should never be said", Ct));

        Assert.Equal(PolicyGateGateway.PolicyDeniedCode, ex.Code);
        Assert.True(await human.StaysQuietAsync(TimeSpan.FromMilliseconds(500), Ct), "a denied call must not reach the GC");
    }

    [Fact]
    public async Task Policy_denies_an_unlisted_model_before_any_provider_call()
    {
        // Needs no server: the GW7 resolver and the GW10 adapter are merged, so this part of the exit check runs for real today.
        var gateway = Gateway();
        using var denied = new GatewayChatClient(gateway, ExitCheckPolicyWorld.AgentId, ExitCheckPolicyWorld.DeniedModel);
        using var unknownAgent = new GatewayChatClient(gateway, "nobody", ExitCheckPolicyWorld.AllowedModel);

        var ex = await Assert.ThrowsAsync<GatewayModelException>(async () =>
            await denied.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));
        var ex2 = await Assert.ThrowsAsync<GatewayModelException>(async () =>
            await unknownAgent.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Equal(PolicyGateGateway.PolicyDeniedCode, ex.Code);
        Assert.Contains("ModelNotAllowed", ex.Message);
        Assert.Equal(PolicyGateGateway.PolicyDeniedCode, ex2.Code);
        Assert.Empty(_provider.Calls);
    }

    [Fact]
    public void The_ledger_lists_every_server_piece_the_plan_names()
    {
        string[] planned = ["G1", "G2", "G3", "HOST1", "W3", "W4", "W5", "W6", "PRH", "GLUE"];
        Assert.Equal(planned.Order(), ExitCheckLedger.ServerSide.Select(p => p.Id).Order());
    }
}
