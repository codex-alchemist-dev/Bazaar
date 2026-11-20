using System.Collections.Immutable;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Agents.Turns.Tools;
using Bazaar.Server.CodingGit;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.Host.Coding;
using Bazaar.Server.Identity;
using Bazaar.Server.Security.Quarantine;
using Bazaar.Server.Security.Taint;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SessionTaint = Bazaar.Server.Security.Taint.SessionTaint;

namespace Bazaar.Server.Host.Tests.Coding;

public sealed class AgentPortsTests
{
    private static readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private static readonly TimeSpan _timeout = TimeSpan.FromMilliseconds(300);

    // ---- git author ----

    private static ConfiguredAgentGitIdentity Identity(string? name, string email = "123+app[bot]@noreply.example") =>
        new(new ConfiguredAgentDisplayNames(name is null ? new Dictionary<string, string>() : new() { ["a1"] = name }),
            new AgentGitIdentityOptions { AppNoreplyEmail = email });

    [Fact]
    public async Task Author_is_the_display_name_and_the_configured_noreply_email()
    {
        var author = await Identity("Charles").GetAuthorAsync("a1", _ct);

        Assert.Equal(new GitAuthor("Charles", "123+app[bot]@noreply.example"), author);
    }

    [Fact]
    public async Task An_agent_with_no_name_has_no_author()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Identity(null).GetAuthorAsync("a1", _ct));
    }

    [Theory]
    [InlineData("Ev<il>")]
    [InlineData("Line\nBreak")]
    [InlineData("   ")]
    public async Task An_unsafe_name_is_refused(string name)
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Identity(name).GetAuthorAsync("a1", _ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-at-sign")]
    [InlineData("a@b@c")]
    [InlineData("a b@c")]
    [InlineData("<a@c>")]
    public void A_missing_or_malformed_email_stops_construction_naming_the_key(string? email)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Identity("x", email!));

        Assert.Contains("Coding:Harness:Git:AppNoreplyEmail", ex.Message, StringComparison.Ordinal);
    }

    // ---- state reader ----

    private sealed class FakeFreeze(bool frozen = false, bool throws = false) : IAgentFreeze
    {
        public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken cancellationToken) =>
            throws ? throw new InvalidOperationException("down") : ValueTask.FromResult(frozen);

        public ValueTask FreezeAsync(string agentId, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class FakeTaint : ISessionTaintStore
    {
        public Dictionary<Guid, SessionTaint> Sessions { get; } = [];

        public bool Throws { get; set; }

        public bool Hangs { get; set; }

        public async Task<SessionTaint> LoadAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            if (Hangs)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return Throws ? throw new InvalidOperationException("down") : Sessions.GetValueOrDefault(sessionId, SessionTaint.Clean);
        }
    }

    private static RunningTurnInfo Turn(Guid agent, Guid session, string? gc = "gc-1", int depth = 0, params string[] tools) =>
        new(Guid.NewGuid(), agent, session, "h", tools, depth, 5, gc, 1, "");

    private static SessionTaint Tainted(TaintState state, params ContentTrust[] trusts) =>
        new(state, [.. trusts.Select(t => new TaintSource(Guid.NewGuid(), t, state))]);

    private sealed record Rig(InProcessRunningTurnRegistry Turns, InMemoryQuarantineStore Quarantine, FakeTaint Taint, AgentStateReader Reader);

    private static Rig MakeRig(FakeFreeze? freeze = null, IQuarantineStore? quarantine = null)
    {
        var turns = new InProcessRunningTurnRegistry();
        var store = new InMemoryQuarantineStore();
        var taint = new FakeTaint();
        return new Rig(turns, store, taint, new AgentStateReader(quarantine ?? store, freeze ?? new FakeFreeze(), turns, taint, _timeout));
    }

    [Fact]
    public void A_clean_agent_with_no_running_turn_is_clean_and_not_quarantined()
    {
        var rig = MakeRig();
        var agent = Guid.NewGuid().ToString("D");

        Assert.False(rig.Reader.IsQuarantined(agent));
        Assert.Equal(TaintState.Clean, rig.Reader.TaintFor(agent).State);
    }

    [Fact]
    public async Task An_active_agent_quarantine_record_quarantines_and_a_release_lifts_it()
    {
        var rig = MakeRig();
        var agent = Guid.NewGuid().ToString("D");
        var record = new QuarantineRecord(Guid.NewGuid(), new QuarantineSubject(QuarantineSubjectKind.Agent, agent), QuarantineReason.OwnerAction, new QuarantineActor("o", QuarantineActorKind.Owner), null);
        Assert.True(await rig.Quarantine.TryInsertActiveAsync(record, _ct));

        Assert.True(rig.Reader.IsQuarantined(agent));

        await rig.Quarantine.TryReleaseAsync(record.Id, new QuarantineActor("o", QuarantineActorKind.Owner), _ct);
        Assert.False(rig.Reader.IsQuarantined(agent));
    }

    [Fact]
    public void A_frozen_agent_counts_as_quarantined()
    {
        Assert.True(MakeRig(new FakeFreeze(frozen: true)).Reader.IsQuarantined(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void A_failing_freeze_read_fails_closed()
    {
        Assert.True(MakeRig(new FakeFreeze(throws: true)).Reader.IsQuarantined(Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void A_blank_agent_id_is_quarantined()
    {
        Assert.True(MakeRig().Reader.IsQuarantined(" "));
    }

    [Fact]
    public void Taint_is_the_highest_among_the_agents_running_sessions()
    {
        var rig = MakeRig();
        var agent = Guid.NewGuid();
        var (s1, s2, other) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var r1 = rig.Turns.Register(Turn(agent, s1));
        using var r2 = rig.Turns.Register(Turn(agent, s2));
        using var r3 = rig.Turns.Register(Turn(Guid.NewGuid(), other));
        rig.Taint.Sessions[s1] = Tainted(TaintState.Soft, ContentTrust.Agent);
        rig.Taint.Sessions[s2] = Tainted(TaintState.Hard, ContentTrust.External);
        rig.Taint.Sessions[other] = Tainted(TaintState.Hard, ContentTrust.External);

        var result = rig.Reader.TaintFor(agent.ToString("D"));

        Assert.Equal(TaintState.Hard, result.State);
        Assert.Equal(2, result.Sources.Length);
    }

    [Fact]
    public void An_unreadable_session_makes_the_agent_hard()
    {
        var rig = MakeRig();
        var agent = Guid.NewGuid();
        using var r = rig.Turns.Register(Turn(agent, Guid.NewGuid()));
        rig.Taint.Throws = true;

        Assert.Equal(TaintState.Hard, rig.Reader.TaintFor(agent.ToString("D")).State);
    }

    [Fact]
    public void A_taint_read_that_times_out_makes_the_agent_hard()
    {
        var rig = MakeRig();
        var agent = Guid.NewGuid();
        using var r = rig.Turns.Register(Turn(agent, Guid.NewGuid()));
        rig.Taint.Hangs = true;

        Assert.Equal(TaintState.Hard, rig.Reader.TaintFor(agent.ToString("D")).State);
    }

    [Fact]
    public void An_agent_id_that_is_not_a_guid_is_hard()
    {
        Assert.Equal(TaintState.Hard, MakeRig().Reader.TaintFor("not-a-guid").State);
    }

    [Fact]
    public void A_quarantine_store_that_fails_fails_closed()
    {
        var rig = MakeRig(quarantine: new ThrowingQuarantineStore());

        Assert.True(rig.Reader.IsQuarantined(Guid.NewGuid().ToString("D")));
    }

    private sealed class ThrowingQuarantineStore : IQuarantineStore
    {
        public Task<QuarantineRecord?> GetAsync(Guid id, CancellationToken ct = default) => throw new InvalidOperationException("down");

        public Task<QuarantineRecord?> GetActiveAsync(QuarantineSubject subject, CancellationToken ct = default) => throw new InvalidOperationException("down");

        public Task<bool> TryInsertActiveAsync(QuarantineRecord record, CancellationToken ct = default) => throw new InvalidOperationException("down");

        public Task<QuarantineRecord?> TryReleaseAsync(Guid id, QuarantineActor releasedBy, CancellationToken ct = default) => throw new InvalidOperationException("down");
    }

    // ---- push session records ----

    private static PushGateRequest Push(Guid agent, string gc = "gc-1") =>
        new(agent.ToString("D"), new RepoRef("repo", "main"), BranchName.Parse("agent/x"), gc, "task-1");

    private static RunningTurnPushSessionRecords Records(InProcessRunningTurnRegistry turns, FakeTaint taint) => new(turns, taint, _timeout);

    [Fact]
    public async Task A_turn_woken_by_the_gc_with_the_push_tool_is_an_actor_session()
    {
        var turns = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        var session = Guid.NewGuid();
        using var r = turns.Register(Turn(agent, session, "gc-1", 0, PermissionGatePushGate.ToolName));
        var taint = new FakeTaint();
        taint.Sessions[session] = Tainted(TaintState.Soft, ContentTrust.System, ContentTrust.Agent);

        var record = await Records(turns, taint).FindAsync(Push(agent), _ct);

        Assert.NotNull(record);
        Assert.Equal(session.ToString("D"), record.SessionId);
        Assert.Equal(ToolSessionRole.Actor, record.Role);
        Assert.Equal("agent", record.SourceTrust);
        Assert.Null(record.Grant);
    }

    [Fact]
    public async Task A_turn_without_the_push_tool_is_a_reader_session()
    {
        var turns = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        using var r = turns.Register(Turn(agent, Guid.NewGuid(), "gc-1", 0, "read"));

        var record = await Records(turns, new FakeTaint()).FindAsync(Push(agent), _ct);

        Assert.Equal(ToolSessionRole.Reader, record!.Role);
    }

    [Fact]
    public async Task No_turn_in_the_gc_means_no_record()
    {
        var turns = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        using var r = turns.Register(Turn(agent, Guid.NewGuid(), "gc-2", 0, PermissionGatePushGate.ToolName));

        Assert.Null(await Records(turns, new FakeTaint()).FindAsync(Push(agent), _ct));
        Assert.Null(await Records(turns, new FakeTaint()).FindAsync(Push(Guid.NewGuid()), _ct));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public async Task A_bad_agent_id_means_no_record(string agentId)
    {
        var request = Push(Guid.NewGuid()) with { AgentId = agentId };

        Assert.Null(await Records(new InProcessRunningTurnRegistry(), new FakeTaint()).FindAsync(request, _ct));
    }

    [Fact]
    public async Task The_actor_turn_is_preferred_then_the_top_level_turn()
    {
        var turns = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        var (reader, child, top) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var r1 = turns.Register(Turn(agent, reader, "gc-1", 0, "read"));
        using var r2 = turns.Register(Turn(agent, child, "gc-1", 2, PermissionGatePushGate.ToolName));
        using var r3 = turns.Register(Turn(agent, top, "gc-1", 0, PermissionGatePushGate.ToolName));

        var record = await Records(turns, new FakeTaint()).FindAsync(Push(agent), _ct);

        Assert.Equal(top.ToString("D"), record!.SessionId);
    }

    [Fact]
    public async Task An_unreadable_taint_gives_the_external_label()
    {
        var turns = new InProcessRunningTurnRegistry();
        var agent = Guid.NewGuid();
        using var r = turns.Register(Turn(agent, Guid.NewGuid(), "gc-1", 0, PermissionGatePushGate.ToolName));

        var record = await Records(turns, new FakeTaint { Throws = true }).FindAsync(Push(agent), _ct);

        Assert.Equal("external", record!.SourceTrust);
    }

    [Theory]
    [InlineData(TaintState.Clean, "agent")]
    [InlineData(TaintState.Hard, "external")]
    public void Source_trust_with_no_sources_follows_the_taint_level(TaintState state, string label)
    {
        Assert.Equal(label, RunningTurnPushSessionRecords.SourceTrustLabel(new SessionTaint(state, ImmutableArray<TaintSource>.Empty)));
    }

    [Theory]
    [InlineData(ContentTrust.Owner, "owner")]
    [InlineData(ContentTrust.System, "system")]
    [InlineData(ContentTrust.MemberHuman, "member-human")]
    [InlineData(ContentTrust.Agent, "agent")]
    [InlineData(ContentTrust.External, "external")]
    [InlineData(ContentTrust.Quarantined, "quarantined")]
    public void Source_trust_is_the_least_trusted_source(ContentTrust worst, string label)
    {
        Assert.Equal(label, RunningTurnPushSessionRecords.SourceTrustLabel(Tainted(TaintState.Soft, ContentTrust.Owner, worst)));
    }

    // ---- registration ----

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void Nothing_is_registered_while_the_harness_is_off()
    {
        var services = new ServiceCollection().AddBazaarCodingHarnessAgentPorts(Config(("Coding:Harness:Enabled", "false")));

        Assert.Empty(services);
    }

    [Fact]
    public void An_enabled_harness_with_no_email_stops_registration_naming_the_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBazaarCodingHarnessAgentPorts(Config(("Coding:Harness:Enabled", "true"))));

        Assert.Contains("Coding:Harness:Git:AppNoreplyEmail", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("61")]
    [InlineData("abc")]
    public void A_bad_timeout_stops_registration_naming_the_key(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddBazaarCodingHarnessAgentPorts(Config(
                ("Coding:Harness:Enabled", "true"),
                ("Coding:Harness:Git:AppNoreplyEmail", "a@b"),
                (CodingHarnessAgentPortsExtensions.ReadTimeoutKey, value))));

        Assert.Contains(CodingHarnessAgentPortsExtensions.ReadTimeoutKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_enabled_harness_binds_the_three_ports_and_the_names_come_from_configuration()
    {
        var config = Config(
            ("Coding:Harness:Enabled", "true"),
            ("Coding:Harness:Git:AppNoreplyEmail", "1+app[bot]@noreply.example"),
            ("Coding:Harness:Git:AgentNames:a1", "Charles"));
        var services = new ServiceCollection()
            .AddSingleton<IQuarantineStore, InMemoryQuarantineStore>()
            .AddSingleton<IAgentFreeze>(new FakeFreeze())
            .AddSingleton<ISessionTaintStore>(new FakeTaint())
            .AddRunningTurnRegistry()
            .AddBazaarCodingHarnessAgentPorts(config);

        Assert.Contains(services, d => d.ServiceType == typeof(IAgentGitIdentity));
        Assert.Contains(services, d => d.ServiceType == typeof(IAgentStateReader));
        Assert.Contains(services, d => d.ServiceType == typeof(IPushSessionRecords));

        using var provider = services.BuildServiceProvider();
        Assert.Equal("Charles", (await provider.GetRequiredService<IAgentGitIdentity>().GetAuthorAsync("a1", _ct)).Name);
        Assert.NotNull(provider.GetRequiredService<IAgentStateReader>());
        Assert.NotNull(provider.GetRequiredService<IPushSessionRecords>());
    }

    [Fact]
    public void A_registration_the_host_made_first_wins()
    {
        var mine = new MineReader();
        var services = new ServiceCollection().AddSingleton<IAgentStateReader>(mine);

        services.AddBazaarCodingHarnessAgentPorts(Config(("Coding:Harness:Enabled", "true"), ("Coding:Harness:Git:AppNoreplyEmail", "a@b")));

        using var provider = services.BuildServiceProvider();
        Assert.Same(mine, provider.GetRequiredService<IAgentStateReader>());
    }

    private sealed class MineReader : IAgentStateReader
    {
        public SessionTaint TaintFor(string agentId) => SessionTaint.Clean;

        public bool IsQuarantined(string agentId) => false;
    }
}
